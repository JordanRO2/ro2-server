using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.GameData;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Inventory.Domain;
using RO2.Characters.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles RMI 1327 (ReqRepairItem): repairs a list of items at an NPC and charges the repair cost.</summary>
/// <remarks>
/// Request: NPC actor handle, followed by a list of items, each identified by slot, bag and uid, and a flag.
/// The reply is RMI 2336 (OnRepairItem): the result, the character head with the updated wallet, and the
/// repaired item records. The server then sends the 2498 stat update.
/// The cost of each item is computed by <see cref="RepairPricing"/> from its missing durability, grade and
/// base price. The guild discount applies when the player's guild has it. Quivers are free.
/// Refusals answer with the refusal form of RMI 2336 and nothing is charged.
/// </remarks>
public class RepairItemHandler : ZoneRmiHandlerBase<RepairItemHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqRepairItem;
    public override string Name => "RepairItem";

    private const int HeaderLength = 10;          // [u32 len][u32 npcHandle][u16 count]
    private const int ElementLength = 11;         // {u8 slot, u8 bag, u64 uid, u8 flag}

    /// <summary>Request offset of the NPC actor handle, after the u32 length prefix.</summary>
    private const int NpcHandleOffset = 4;

    /// <summary>Request offset of the u16 element count, after the length prefix and the NPC handle.</summary>
    private const int CountOffset = 8;

    /// <summary>Offset of the u64 item uid inside an element, after the slot and bag bytes.</summary>
    private const int ElementUidOffset = 2;

    /// <summary>Width of the u64 item uid.</summary>
    private const int ItemUidLength = 8;

    /// <summary>Size of the u32 length prefix; the prefix counts the bytes that follow it.</summary>
    private const int LengthPrefixSize = 4;

    /// <summary>Low byte of an actor handle: the actor type tag (<see cref="NpcStateManager.NpcActorTag"/> for an NPC).</summary>
    private const uint ActorTagMask = 0xFF;

    public RepairItemHandler(IServiceScopeFactory s, ILogger<RepairItemHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        var body = payload.Span;
        if (body.Length < HeaderLength) return;
        uint len = BinaryPrimitives.ReadUInt32LittleEndian(body);
        uint npcHandle = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(NpcHandleOffset));
        int count = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(CountOffset));
        if (len + LengthPrefixSize != body.Length || HeaderLength + ElementLength * count != body.Length)
        {
            Logger.LogWarning("Session {Id}: RepairItem body {Len} B does not frame as len={Prefix} + 6 + 11*{Count}",
                session.SessionId, body.Length, len, count);
            return;
        }

        // The NPC check comes first, before the items are looked at.
        if ((npcHandle & ActorTagMask) != NpcStateManager.NpcActorTag)
        {
            Logger.LogInformation("Session {Id}: {Name} asks handle {Handle:X8} for a repair; not an NPC, code {Code}",
                session.SessionId, player.Name, npcHandle, RepairPricing.ResultNotNpc);
            await SendRefusalAsync(session, RepairPricing.ResultNotNpc);
            return;
        }

        var uids = new ulong[count];
        for (int i = 0; i < count; i++)
            uids[i] = BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(HeaderLength + ElementLength * i + ElementUidOffset, ItemUidLength));

        var inventory = services.GetRequiredService<IInventoryRepository>();
        var store = services.GetRequiredService<GameDataStore>();

        // Resolve and price every requested item the player owns; uids are global, so ownership is checked.
        var repaired = new List<(InventoryItem Item, short MaxDur)>();
        long totalCost = 0;
        foreach (var uid in uids)
        {
            var item = await inventory.GetByIdAsync(uid);
            if (item is null || item.CharacterId != player.CharacterId) continue;
            var info = store.GetItem(item.ItemId);
            short maxDur = (short)Math.Min(info?.Durability ?? 0, (uint)short.MaxValue);
            if (maxDur > 0 && info is not null)
                totalCost += RepairPricing.ItemCost(info, item.Durability < 0 ? maxDur : Math.Min((int)item.Durability, maxDur));
            repaired.Add((item, maxDur));
        }
        if (repaired.Count == 0)
        {
            Logger.LogInformation("Session {Id}: {Name} asks for a repair of {Count} uid(s), none held: code {Code}",
                session.SessionId, player.Name, count, RepairPricing.ResultNoItem);
            await SendRefusalAsync(session, RepairPricing.ResultNoItem);
            return;
        }

        // The guild's repair discount (Guild_Skill_Level category 5).
        await GuildService.Instance.InitAsync(services.GetRequiredService<IGuildStore>());
        long discountPct = 0;
        if (GuildService.Instance.GetByCharId(player.CharacterId) is { } guild)
        {
            var table = GuildSkillLearnPackets.Index(store.GuildSkillLevel);
            lock (guild.Gate) discountPct = RepairPricing.DiscountPercent(guild.Skills, table);
        }
        if (totalCost > 0) totalCost = RepairPricing.ApplyDiscount(totalCost, discountPct);

        // The client checks the wallet itself before sending, so this is only reached by a desynced client.
        // There is no refusal code for money, so nothing is repaired and nothing is sent.
        if (!player.TryDebit(totalCost))   // the cost is taken in the same atomic step that checks it, before the items are touched
        {
            Logger.LogInformation("{Name} cannot afford repair of {Count} items: {Cost} > {Gold}",
                player.Name, repaired.Count, totalCost, player.Gold);
            return;
        }

        var records = new List<byte[]>(repaired.Count);
        foreach (var (item, maxDur) in repaired)
        {
            if (maxDur > 0 && item.Durability != maxDur)
            {
                item.Durability = maxDur;   // repaired records come back at full durability
                await inventory.UpdateItemAsync(item);
            }
            // The record carries the runehole block (record+33..67): the client re-reads each repaired item from it,
            // so a zeroed block would erase the holes and runes it shows.
            records.Add(ItemWorkbench.RecordWithRuneholes(item, store));
        }
        await services.GetRequiredService<ICharacterRepository>().UpdateGoldAsync(player.CharacterId, player.Gold);

        Logger.LogDebug("{Name} repairs {Count} items at NPC {Npc:X8} for {Cost} zeny (guild discount {Pct} %)",
            player.Name, records.Count, npcHandle, totalCost, discountPct);

        // Same builder as the repair-kit path in ItemUseHandler, so both triggers emit one layout.
        var repairBody = RepairItemPackets.BuildOnRepairItem2336(player, records, store);
        await session.SendRmiAsync(RepairItemPackets.OnRepairItem2336, writer => writer.WriteBytes(repairBody));

        // "Item repair number achieved" Kharas (Complete_Type 30) count the items of this repair: one 2577 per accepted
        // such Khara, stepped by the repair's item count and capped at the target, then its 2575 / 2576 when that
        // reaches it, all after the 2336.
        await KharaService.OnRepairedAsync(session, player, records.Count, services, Logger, ct);

        // The 2498 stat sheet keeps the active effects, and the runtime max and combat cache follow it: a repaired
        // item whose durability had run out counts again.
        if (player.Character is { } ch)
        {
            var equipped = await inventory.GetEquipmentAsync(ch.Id);
            await session.SendInternalRmiAsync(ZoneS2COpcodes.OnPcTrait,
                services.GetRequiredService<EffectService>().RefreshPcTraitSheet(player, ch, equipped));
        }
    }

    /// <summary>Builds the refusal reply of RMI 2336: nothing is repaired or charged, and no 2498 follows.</summary>
    private static Task SendRefusalAsync(ProudSession session, ushort code)
    {
        var body = RepairPricing.BuildRepairRefusal2336(code);
        return session.SendRmiAsync(RepairItemPackets.OnRepairItem2336, writer => writer.WriteBytes(body));
    }
}
