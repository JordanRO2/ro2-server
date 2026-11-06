using RO2.Guilds.Domain;
using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Persistence.Mongo.Persistence;
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
/// buys it from the guild funds, stores it on the guild with a random colour, and persists it; picking the same robe
/// again rerolls the colour. A non-master requester receives 2928 with <see cref="GuildRobeChangedPackets.SelectNotMaster"/>
/// and no 2927, as does a guild that cannot afford the robe. A request for any other robe ID receives no answer.
/// </para>
/// </remarks>
public class GuildSelectRobeHandler : ZoneRmiHandlerBase<GuildSelectRobeHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqGuildSelectRobe;
    public override string Name => "GuildSelectRobe";

    /// <summary>
    /// Guild-fund cost of buying a robe. Custom content: no retail price was captured for the robe purchase.
    /// shortcut: a single flat cost for every robe, tune here if robes should cost differently or be priced per entry.
    /// </summary>
    private const long RobePurchaseCost = 100000;
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
        if (robeItemId == GuildRobeRecord.BasicRobeItemId)
        {
            // Picking the Basic Robe reverts the guild to the default robe; reverting is free.
            lock (guild.Gate) { guild.RobeItemId = 0; guild.RobeColorCode = 0; }
            selectionRecord = GuildRobeChangedPackets.BasicRobeSelectedRecord();
            Logger.LogInformation("{Name} reverts guild {Guild} to the Basic Robe", player.Name, guild.Name);
        }
        else
        {
            uint colour = 0xFF000000u | (uint)Random.Shared.Next(0x1000000);   // full alpha, random 24-bit RGB
            bool charged;
            lock (guild.Gate)
            {
                charged = GuildFundsRules.TryTakeFunds(guild, RobePurchaseCost, out _);
                if (charged) { guild.RobeItemId = robeItemId; guild.RobeColorCode = colour; }
            }
            if (!charged)
            {
                Logger.LogInformation("{Name} cannot afford robe {Robe} for guild {Guild} (cost {Cost})",
                    player.Name, robeItemId, guild.Name, RobePurchaseCost);
                await session.SendRmiAsync(ZoneS2COpcodes.AnsGuildSelectRobe,
                    GuildRobeChangedPackets.BuildAnsGuildSelectRobe2928(GuildRobeChangedPackets.SelectNotMaster), ct);
                return;
            }
            selectionRecord = GuildRobeChangedPackets.SelectionRecord(robeItemId, colour, DateTime.Now.ToOADate());
            Logger.LogInformation("{Name} buys robe {Robe} (colour {Colour:X8}) for guild {Guild} at {Cost}",
                player.Name, robeItemId, colour, guild.Name, RobePurchaseCost);
        }

        await repo.SaveAsync(guild, ct);
        await session.SendRmiAsync(ZoneS2COpcodes.AnsGuildSelectRobe,
            GuildRobeChangedPackets.BuildAnsGuildSelectRobe2928(GuildRobeChangedPackets.SelectAccepted), ct);
        await session.SendRmiAsync(ZoneS2COpcodes.NfyGuildRobeChanged,
            GuildRobeChangedPackets.BuildNfyGuildRobeChanged2927(selectionRecord), ct);
    }
}
