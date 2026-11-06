using RO2.Guilds.Domain;
using System.Buffers.Binary;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Persistence.Mongo.Persistence;
using RO2.Inventory.Application;
using RO2.Inventory.Domain;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1633 ReqGuildSelectRobe: the guild master selects the robe the guild wears. The answer is 2928 AnsGuildSelectRobe, followed on success by a 2927 NfyGuildRobeChanged.
/// </summary>
/// <remarks>
/// <para>
/// The robe is identified by the item ID of the selected entry in the 2929 robe list. Only the guild master receives a code-0 2929 and can open the robe selection window (see <see cref="GuildListRegisteredRobeHandler"/>).
/// </para>
/// <para>
/// Picking the Basic Robe reverts the guild to the default robe for free. Picking one of the Guild_Robe table robes
/// buys it with Guild Tokens taken from the master's own bag, the currency and per-robe amount the robe NPC charges
/// (BuyTokenInfo, keyed by the robe item id), stores it on the guild with a random colour, and persists it; picking the
/// same robe again rerolls the colour. A non-master requester receives 2928 with <see cref="GuildRobeChangedPackets.SelectNotMaster"/>
/// and no 2927, as does a master whose bag holds too few tokens. A request for any other robe ID receives no answer.
/// </para>
/// </remarks>
public class GuildSelectRobeHandler : ZoneRmiHandlerBase<GuildSelectRobeHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqGuildSelectRobe;
    public override string Name => "GuildSelectRobe";

    /// <summary>Offset of the count byte in a 126-byte item record; forced to 0 so an emptied token stack clears its cell.</summary>
    private const int ItemRecordCountOffset = 14;

    public GuildSelectRobeHandler(IServiceScopeFactory s, ILogger<GuildSelectRobeHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {

        if (payload.Length != 4)
        {
            Logger.LogWarning("{Name}: 1633 body is {Len} bytes (the client writes 4), ignored", player.Name, payload.Length);
            return;
        }
        uint robeItemId = BinaryPrimitives.ReadUInt32LittleEndian(payload.Span);

        var repo = services.GetRequiredService<IGuildRepository>();
        await GuildService.Instance.InitAsync(repo);
        var guild = GuildService.Instance.GetByCharId(player.CharacterId);
        if (guild == null || guild.MasterCharId != player.CharacterId)
        {
            Logger.LogWarning("{Name} asked to select guild robe {Robe} without being a guild master", player.Name, robeItemId);
            await session.SendRmiAsync(ZoneS2COpcodes.AnsGuildSelectRobe,
                GuildRobeChangedPackets.BuildAnsGuildSelectRobe2928(GuildRobeChangedPackets.SelectNotMaster), ct);
            return;
        }

        var data = services.GetRequiredService<RO2.GameData.GameDataStore>();
        if (!GuildRobeRecord.SelectableRobeItemIds(data).Contains(robeItemId))
        {
            Logger.LogWarning("{Name} asked to select guild robe {Robe}, which is not a pickable robe", player.Name, robeItemId);
            return;
        }

        byte[] selectionRecord;
        IReadOnlyList<byte[]> tokenRecords = System.Array.Empty<byte[]>();
        if (robeItemId == GuildRobeRecord.BasicRobeItemId)
        {
            // Picking the Basic Robe reverts the guild to the default robe; reverting is free.
            lock (guild.Gate) { guild.RobeItemId = 0; guild.RobeColorCode = 0; }
            selectionRecord = GuildRobeChangedPackets.BasicRobeSelectedRecord();
            Logger.LogInformation("{Name} reverts guild {Guild} to the Basic Robe", player.Name, guild.Name);
        }
        else
        {
            var cost = data.BuyTokenInfo.FirstOrDefault(r => r.Item_ID == robeItemId);
            if (cost is null || cost.TokenItemID1 == 0 || cost.NeedValue1 == 0)
            {
                Logger.LogWarning("{Name} picked robe {Robe} but it has no Guild Token price (BuyTokenInfo); refused", player.Name, robeItemId);
                await session.SendRmiAsync(ZoneS2COpcodes.AnsGuildSelectRobe,
                    GuildRobeChangedPackets.BuildAnsGuildSelectRobe2928(GuildRobeChangedPackets.SelectNotMaster), ct);
                return;
            }

            var charged = await TryChargeRobeTokensAsync(services, player.CharacterId, cost.TokenItemID1, cost.NeedValue1);
            if (charged is null)
            {
                Logger.LogInformation("{Name} cannot afford robe {Robe} for guild {Guild} ({Need} of token {Token})",
                    player.Name, robeItemId, guild.Name, cost.NeedValue1, cost.TokenItemID1);
                await session.SendRmiAsync(ZoneS2COpcodes.AnsGuildSelectRobe,
                    GuildRobeChangedPackets.BuildAnsGuildSelectRobe2928(GuildRobeChangedPackets.SelectNotMaster), ct);
                return;
            }
            tokenRecords = charged;

            uint colour = 0xFF000000u | (uint)Random.Shared.Next(0x1000000);   // full alpha, random 24-bit RGB
            lock (guild.Gate) { guild.RobeItemId = robeItemId; guild.RobeColorCode = colour; }
            selectionRecord = GuildRobeChangedPackets.SelectionRecord(robeItemId, colour, DateTime.Now.ToOADate());
            Logger.LogInformation("{Name} buys robe {Robe} (colour {Colour:X8}) for guild {Guild}: {Need} of token {Token}",
                player.Name, robeItemId, colour, guild.Name, cost.NeedValue1, cost.TokenItemID1);
        }

        await repo.SaveAsync(guild, ct);
        // Reflect the spent tokens in the bag UI before the robe answer, as the token shop buy does: one 2379 per
        // token stack touched, reason 2 (token buy).
        foreach (var rec in tokenRecords)
            await session.SendRmiAsync(ZoneS2COpcodes.OnDestroyItem,
                writer => writer.WriteBytes(ShopItemPackets.BuildDestroyItem2379(player.EntityId, rec, ShopItemPackets.DestroyReasonTokenBuy)));
        await session.SendRmiAsync(ZoneS2COpcodes.AnsGuildSelectRobe,
            GuildRobeChangedPackets.BuildAnsGuildSelectRobe2928(GuildRobeChangedPackets.SelectAccepted), ct);
        await session.SendRmiAsync(ZoneS2COpcodes.NfyGuildRobeChanged,
            GuildRobeChangedPackets.BuildNfyGuildRobeChanged2927(selectionRecord), ct);
    }

    /// <summary>
    /// Takes <paramref name="need"/> of token <paramref name="tokenItemId"/> from the character's own bag under its
    /// inventory gate, drawing lowest cell first. Returns the 2379 records of the stacks it emptied or reduced, or null
    /// when the bag holds too few (nothing is taken then).
    /// </summary>
    private static async Task<IReadOnlyList<byte[]>?> TryChargeRobeTokensAsync(
        IServiceProvider services, uint characterId, uint tokenItemId, int need)
    {
        var inv = services.GetRequiredService<IInventoryRepository>();
        return await CharacterInventoryGate.RunAsync(characterId, async () =>
        {
            var bag = await inv.GetByCharacterAsync(characterId);
            var stacks = bag.Where(i => i.StorageType == 0)
                .Select(i => new BagStack(i.Id, i.ItemId, i.Count, i.Slot)).ToList();
            var takes = BagStackPlanner.TakeFromStacks(stacks, tokenItemId, need, 0);
            if (takes is null) return (IReadOnlyList<byte[]>?)null;

            var records = new List<byte[]>(takes.Count);
            foreach (var t in takes)
            {
                var row = bag.First(i => i.Id == t.StackId);
                row.Count = t.Left;
                if (t.Left <= 0) await inv.RemoveItemAsync(t.StackId);
                else await inv.UpdateItemAsync(row);
                var record = ItemPackets.BuildItemRecord126(row);
                if (t.Left <= 0) record[ItemRecordCountOffset] = 0;   // the record writer clamps to >= 1; force 0 to clear the cell
                records.Add(record);
            }
            return records;
        });
    }
}
