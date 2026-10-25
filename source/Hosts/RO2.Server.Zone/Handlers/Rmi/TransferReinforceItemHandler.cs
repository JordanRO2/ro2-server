using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Inventory.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1682 ReqTransferReinforceItem - move an item's refine grade onto another item with a refine
/// transfer ticket, destroying the source.</summary>
/// <remarks>
/// <para>
/// REQUEST. 24 bytes, no envelope: <c>[u64 ticketUid][u64 targetUid][u64 sourceUid]</c>. TRIGGER: the start
/// button of the client's refine-transfer window, which sends its three cells in that order. The window's
/// preview, gates and fee are in <see cref="RefineTransferRules"/>.
/// </para>
/// <para>
/// RESPONSE, with each item resolved from the last record the session carried for its uid:
/// </para>
/// <list type="bullet">
/// <item>@0 is a refine transfer ticket (ItemInfo Item_Type 118, a REFINE_Trans_ITEM row).</item>
/// <item>SUCCESS: 2379 OnDestroyItem on the @16 source with a stripped record (<see cref="DestroyedSourceRecord"/>),
///   2329 OnUseItem on the ticket (result 0, useSource 1, remaining 0), 2498 OnPcTrait (the full 143-byte sheet,
///   restating the current one), then 3014 = <c>[u16 0][126-byte record of the @8 target]</c> whose record+0x1D
///   (InventoryItem.Refine) is the new grade; a target stack keeps its count.</item>
/// <item>REFUSAL: 3014 result 25108 over an all-zero record, alone - no 2379, 2329 or 2498.</item>
/// </list>
/// </remarks>
public class TransferReinforceItemHandler : ZoneRmiHandlerBase<TransferReinforceItemHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqTransferReinforceItem;
    public override string Name => "TransferReinforceItem";

    /// <summary>1682 body length: 24 bytes.</summary>
    private const int RequestBodyLength = 24;

    /// <summary>Request +0: u64 uid of the refine transfer ticket.</summary>
    private const int TicketUidOffset = 0;

    /// <summary>Request +8: u64 uid of the target item, which receives the grade.</summary>
    private const int TargetUidOffset = 8;

    /// <summary>Request +16: u64 uid of the source item, which is destroyed.</summary>
    private const int SourceUidOffset = 16;

    /// <summary>3014 result of an accepted transfer; the record that follows is the target's.</summary>
    private const ushort TransferOk = 0;

    /// <summary>The only non-zero 3014 result, always over a zeroed record.</summary>
    internal const ushort TransferRefused = 25108;

    /// <summary>2329 OnUseItem useSource of a ticket consumed by this window (the item-skill path sends 0).</summary>
    private const byte TicketUseSource = 1;

    /// <summary>InventoryItem.StorageType of the bag, the only storage the three items may come from.</summary>
    private const byte StorageTypeBag = 0;

    /// <summary>Length of the 3014 result field, which precedes the item record.</summary>
    private const int ResultFieldLength = sizeof(ushort);

    // ---- 126-byte item record offsets ----

    /// <summary>Record +0x00: u64 item uid.</summary>
    private const int RecordUidOffset = 0x00;

    /// <summary>Length of the uid (u64 at +0x00) and template id (u32 at +0x08) that open a record.</summary>
    private const int RecordUidAndTemplateLength = 12;

    /// <summary>Record +0x0F: client bag container byte.</summary>
    private const int RecordContainerOffset = 0x0F;

    /// <summary>Record +0x10: client bag slot byte.</summary>
    private const int RecordSlotOffset = 0x10;

    public TransferReinforceItemHandler(IServiceScopeFactory s, ILogger<TransferReinforceItemHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < RequestBodyLength)
        {
            Logger.LogWarning("Session {Id}: TransferReinforceItem body is {Len} bytes, not the 24 retail sends",
                session.SessionId, payload.Length);
            return;
        }
        var span = payload.Span;
        ulong ticketUid = BinaryPrimitives.ReadUInt64LittleEndian(span[TicketUidOffset..]);
        ulong targetUid = BinaryPrimitives.ReadUInt64LittleEndian(span[TargetUidOffset..]);
        ulong sourceUid = BinaryPrimitives.ReadUInt64LittleEndian(span[SourceUidOffset..]);

        var inventory = services.GetRequiredService<IInventoryRepository>();
        var store = services.GetRequiredService<GameDataStore>();
        bool distinct = ticketUid != targetUid && ticketUid != sourceUid && targetUid != sourceUid;
        var ticket = distinct ? await inventory.GetByIdAsync(ticketUid) : null;
        var target = distinct ? await inventory.GetByIdAsync(targetUid) : null;
        var source = distinct ? await inventory.GetByIdAsync(sourceUid) : null;

        string? why = Check(store, RefineTransferRules.ItemLevel, player.CharacterId, ticket, target, source, out var terms);
        if (why is not null)
        {
            Logger.LogInformation("{Name} transfer-reinforce {Source} -> {Target} with {Ticket} refused: {Why}",
                player.Name, sourceUid, targetUid, ticketUid, why);
            await session.SendRmiAsync(ZoneS2COpcodes.AnsTransferReinforceItem, BuildAnswer3014(TransferRefused, null), ct);
            return;
        }

        byte oldGrade = target!.Refine;
        byte grade = RefineTransferRules.Outcome(terms);
        target.Refine = grade;
        await inventory.UpdateItemAsync(target);

        // Destroy one source: 2379 with the stack as it now stands. Emptied (count 0),
        // the record is stripped to uid/template/bag/slot.
        int sourceLeft = source!.Count - 1;
        byte[] sourceRecord;
        if (sourceLeft <= 0)
        {
            sourceRecord = DestroyedSourceRecord(source);
            await inventory.RemoveItemAsync(source.Id);
        }
        else
        {
            // A source stack of more than one is shrunk in place, the convention of the
            // break-down 2379 (ShopItemPackets.BuildDestroyItem2379).
            source.Count = sourceLeft;
            await inventory.UpdateItemAsync(source);
            sourceRecord = WorldEntryService.BuildItemRecord126(source);
        }
        await session.SendRmiAsync(ZoneS2COpcodes.OnDestroyItem,
            writer => writer.WriteBytes(ShopItemPackets.BuildDestroyItem2379(player.EntityId, sourceRecord)));

        // Consume the ticket: 2329 OnUseItem, result 0, useSource 1, remaining = the stack after.
        int ticketLeft = ticket!.Count - 1;
        var (tBag, tSlot) = ItemWorkbench.ClientBagSlot(ticket);
        if (ticketLeft <= 0) { await inventory.RemoveItemAsync(ticket.Id); ticketLeft = 0; }
        else { ticket.Count = ticketLeft; await inventory.UpdateItemAsync(ticket); }
        await session.SendRmiAsync(ZoneS2COpcodes.OnUseItem, writer => writer.WriteBytes(
            CombatPackets.BuildUseItem2329(player.EntityId, tSlot, tBag, ticket.Id, (byte)Math.Clamp(ticketLeft, 0, byte.MaxValue), useSource: TicketUseSource)));

        // 2498 between the 2329 and the 3014. The three items are in the bag,
        // so the sheet does not change; it is the in-world sheet path (EffectService.RefreshPcTraitSheet) that
        // keeps the active effects and the runtime max in step.
        if (player.Character is { } ch)
        {
            var equipped = await inventory.GetEquipmentAsync(ch.Id);
            await session.SendInternalRmiAsync(ZoneS2COpcodes.OnPcTrait,
                services.GetRequiredService<EffectService>().RefreshPcTraitSheet(player, ch, equipped));
        }

        // AnsTransferReinforceItem (3014): FIXED 128-byte body, NONE family = [u16 result][126-byte target record],
        // with the runehole block an equipment target carries (kind 1 holes at record+33).
        await session.SendRmiAsync(ZoneS2COpcodes.AnsTransferReinforceItem,
            BuildAnswer3014(TransferOk, ItemWorkbench.RecordWithRuneholes(target, store)), ct);

        Logger.LogInformation("{Name} transferred refine from {Source} ({SourceUid}) onto {Target} ({TargetUid}) with ticket {Ticket}: +{Old} -> +{Grade} (range {Low}..{High})",
            player.Name, source.ItemId, source.Id, target.ItemId, target.Id, ticket.ItemId, oldGrade, grade, terms.Low, terms.High);
    }

    /// <summary>
    /// Why this server refuses the transfer (3014 <see cref="TransferRefused"/>), or null with the client's
    /// <paramref name="terms"/> when it goes ahead. <paramref name="itemLevelOf"/> gives a template's ItemInfo
    /// Item_Lv (ItemData does not carry it). Every value comes from data or the item records.
    /// </summary>
    internal static string? Check(GameDataStore store, Func<uint, int?> itemLevelOf, uint characterId,
        InventoryItem? ticket, InventoryItem? target, InventoryItem? source, out RefineTransferRules.Terms terms)
    {
        terms = default;
        if (!InBag(ticket, characterId) || !InBag(target, characterId) || !InBag(source, characterId))
            return "ticket, target and source must be three distinct stacks in the bag";

        uint ticketId = ticket.ItemId, targetId = target.ItemId, sourceId = source.ItemId;
        var rule = store.GetItem(ticketId)?.Item_Type == RefineTransferRules.TicketItemType
            ? store.REFINETransITEM.FirstOrDefault(r => r.ITEM_ID == ticketId)
            : null;
        if (rule is null)
            return $"item {ticketId} is not a refine transfer ticket";

        // Server-side rule: two cards of one EnforceID, or two non-cards (RefineTransferRules.SameFamily).
        if (!RefineTransferRules.SameFamily(store, targetId, sourceId))
            return $"source {sourceId} and target {targetId} are not of one family (CardEnforceInfo EnforceID, or both equipment)";

        var targetInfo = store.GetItem(targetId);
        var sourceInfo = store.GetItem(sourceId);
        if (targetInfo is null || sourceInfo is null || itemLevelOf(targetId) is not int targetLv || itemLevelOf(sourceId) is not int sourceLv)
            return $"no ItemInfo row (or Item_Lv) for target {targetId} or source {sourceId}";
        // Defensive Equip_Slot 1..7 rule for non-card pairs only (RefineTransferRules.EquipSlotGate).
        if (RefineTransferRules.EquipSlotGate(store, targetId, sourceId) is { } slotWhy)
            return slotWhy;
        if (RefineTransferRules.Config(store.ConfigDef) is not { } config)
            return "Config_Def 83/107/108 missing";

        if (RefineTransferRules.ClientGate(rule, source.Refine, target.Refine, targetInfo.Grade, sourceInfo.Grade, targetLv, sourceLv) is { } gate)
            return gate;

        terms = RefineTransferRules.Compute(rule, source.Refine, target.Refine, targetLv, sourceLv, config);
        // A DISC_RATE-0 ticket (70000889, 60000802, 22402007) charges job-mastery points. This server keeps none
        // (WorldEntryService.JobMasteryPoints is 0), and the client enables its start button only when the points
        // / 1000 cover the fee, so it cannot send one; refuse rather than transfer for free.
        if (terms.Fee > 0)
            return $"ticket {ticketId} charges {terms.Fee} mastery points, which this server does not keep";
        return null;
    }

    /// <summary>
    /// The 2379 record of an emptied source: uid, template, bag and slot, every other byte zero - durability, count,
    /// bind flags (+0x1B) and grade (+0x1D) included. The count 0 is what empties the
    /// client's cell.
    /// </summary>
    internal static byte[] DestroyedSourceRecord(InventoryItem source)
    {
        var full = WorldEntryService.BuildItemRecord126(source);
        var record = new byte[ItemPackets.ItemRecordLength];
        full.AsSpan(RecordUidOffset, RecordUidAndTemplateLength).CopyTo(record);   // +0x00 uid, +0x08 template
        record[RecordContainerOffset] = full[RecordContainerOffset];               // +0x0F bag
        record[RecordSlotOffset] = full[RecordSlotOffset];                         // +0x10 slot
        return record;
    }

    /// <summary>3014: <c>[u16 result][126-byte record]</c>, 128 bytes; a null record is the refusal's all-zero one.</summary>
    internal static byte[] BuildAnswer3014(ushort result, byte[]? record126)
    {
        var body = new byte[ResultFieldLength + ItemPackets.ItemRecordLength];
        BinaryPrimitives.WriteUInt16LittleEndian(body, result);
        record126?.AsSpan(0, ItemPackets.ItemRecordLength).CopyTo(body.AsSpan(ResultFieldLength));
        return body;
    }

    private static bool InBag([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] InventoryItem? it, uint characterId) =>
        it is not null && it.CharacterId == characterId && it.StorageType == StorageTypeBag && it.Count >= 1;
}
