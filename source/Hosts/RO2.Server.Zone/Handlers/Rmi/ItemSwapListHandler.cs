using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Application;
using RO2.Persistence.Mongo.Repositories;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Inventory.Application;
using RO2.Inventory.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>One 13-byte entry of a 1325 batch (the wire order is slot first, then bag, for both ends).</summary>
internal readonly record struct SwapListEntry(byte SrcSlot, byte SrcBag, ulong Uid, byte DstSlot, byte DstBag);

/// <summary>Why <see cref="ItemSwapListHandler.Plan"/> refused a 1325 batch; <see cref="None"/> = accepted.</summary>
internal enum SwapListRefusal
{
    None,
    /// <summary>The declared entry count does not match the entries the frame carries.</summary>
    Malformed,
    /// <summary>An end's (bag, slot) is outside the client's inventory containers (bag 1..4, slot per InventoryRequest).</summary>
    BadCoordinate,
    /// <summary>An entry names a uid the character does not own.</summary>
    UnknownItem,
    /// <summary>One uid appears twice.</summary>
    DuplicateItem,
    /// <summary>An entry's target, or the row it moves, is not a StorageType-0 bag cell (the sort only reshuffles the bag).</summary>
    NotBagCell,
    /// <summary>A target bag cell does not exist (no equipped bag of that size).</summary>
    CellDoesNotExist,
    /// <summary>Two entries land on one cell, or an entry lands on a cell a row that is not moving still holds.</summary>
    CellCollision,
    /// <summary>The batch was valid but a repository write failed; the batch was rolled back.</summary>
    PersistFailed,
}

/// <summary>
/// RMI 1325 ReqPCItemSwapList — the inventory FILTER / sort button. The client sends the whole batch of
/// item moves it wants; the server answers with 2328 OnPCItemSwapList and the client applies it.
///
/// (Was mis-handled by EquipItemHandler, which answered 2336 OnRepairItem — hence the "Item is repaired"
/// popup. 1325 is the batch reposition, not an equip.)
///
/// Body (raw u32-length): [u32 innerLen = bodyLen-4][u32 result/handle][u16 count][count × 13-byte entry].
/// Entry (13 bytes):
///   [0]=srcSlot  [1]=srcBag  [2..9]=uid  [10]=flag  [11]=dstSlot  [12]=dstBag.
/// The client identifies each item by its SOURCE cell (it does not read the uid on apply): it grabs the
/// widget at (srcSlot, srcBag) and drops it at (dstSlot, dstBag). It applies our 2328 when the result field
/// (bytes 4..7) is &lt;= 0.
///
/// The client's own filter computes the whole arrangement (client-side) and sends it. We keep it client-side
/// — the simplest path, no override — and PERSIST each item's destination so the layout survives relog, then
/// echo the request verbatim (result field zeroed) for the client to apply.
///
/// INV-H1 hardening: the batch is all-or-nothing against a simulated end layout (<see cref="Plan"/>). The sort only
/// reshuffles the bag: every source and
/// every destination is bag 1 slot 50..69 or bag 2..4 slot 0..34, i.e. StorageType 0, with no repeated destination.
/// So an entry that targets the equipment window, the character-bag window or a bag cell that does not exist, two
/// entries on one cell, or an unowned/repeated uid refuses the WHOLE batch and nothing moves: the answer is the same
/// echo with a positive result, which the client does not apply (&gt; 0 returns without applying). A worn
/// cell can therefore never change here, so no stat refresh is needed.
/// </summary>
public class ItemSwapListHandler : ZoneRmiHandlerBase<ItemSwapListHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqPCItemSwapList;
    public override string Name => "ItemSwapList";
    private const int EntrySize = 13;

    /// <summary>The 2328 result of a refused batch. The client
    /// only distinguishes &lt;= 0 (apply) from &gt; 0 (ignore), so any positive value has the same client effect.</summary>
    internal const int RefusedResult = 1;

    public ItemSwapListHandler(IServiceScopeFactory s, ILogger<ItemSwapListHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        var entries = new List<SwapListEntry>();
        int declared;
        {
            var span = payload.Span;
            if (span.Length < 10) return;                 // [u32 len][u32 result][u16 count]
            declared = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(8));
            int off = 10;
            for (int i = 0; i < declared && off + EntrySize <= span.Length; i++, off += EntrySize)
                entries.Add(new SwapListEntry(span[off], span[off + 1],
                    BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(off + 2)), span[off + 11], span[off + 12]));
        }

        var inventory = services.GetRequiredService<IInventoryRepository>();
        var gameData = services.GetRequiredService<GameDataStore>();
        // Same per-character gate as 1324: the plan and its writes must see one consistent layout.
        var refusal = await CharacterInventoryGate.RunAsync(player.CharacterId, async () =>
        {
            var rows = await inventory.GetByCharacterAsync(player.CharacterId);
            var verdict = Plan(entries, declared, player.CharacterId, rows, gameData.GetItem, out var moves);
            if (verdict != SwapListRefusal.None) return verdict;
            return await PersistAsync(inventory, moves, Logger) ? SwapListRefusal.None : SwapListRefusal.PersistFailed;
        });
        if (refusal == SwapListRefusal.None)
            Logger.LogDebug("{Name} applied batch of {Count} for {Player}", Name, entries.Count, player.Name);
        else
            Logger.LogWarning("{Name} refused batch of {Count} for {Player}: {Reason}", Name, entries.Count, player.Name, refusal);

        // 2328 OnPCItemSwapList — echo the request verbatim with the result field (bytes 4..7): 0 = apply, > 0 = refused.
        var resp = payload.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(resp.AsSpan(4), refusal == SwapListRefusal.None ? 0 : RefusedResult);
        await session.SendRmiAsync(ZoneS2COpcodes.OnPCItemSwapList, w => w.WriteBytes(resp));
    }

    /// <summary>
    /// Builds the simulated end layout of a 1325 batch from the character's rows and refuses the whole batch when it is
    /// not a legal reshuffle of the bag (see the class remarks); pure, no I/O. On success <paramref name="moves"/> is
    /// every row whose cell actually changes, with its new flat bag slot.
    /// </summary>
    /// <param name="declaredCount">The frame's declared entry count; must equal <paramref name="entries"/>.Count.</param>
    /// <param name="rows">Everything the character owns (any StorageType); rows of other characters are ignored.</param>
    internal static SwapListRefusal Plan(IReadOnlyList<SwapListEntry> entries, int declaredCount, uint characterId,
        IReadOnlyCollection<InventoryItem> rows, Func<uint, ItemData?> lookup, out List<(InventoryItem Row, short Flat)> moves)
    {
        moves = [];
        if (entries.Count != declaredCount) return SwapListRefusal.Malformed;

        var owned = new Dictionary<ulong, InventoryItem>();
        foreach (var r in rows)
            if (r.CharacterId == characterId) owned[r.Id] = r;
        var mover = new HashSet<ulong>();
        var landing = new HashSet<int>();
        var planned = new List<(InventoryItem Row, short Flat)>();

        foreach (var e in entries)
        {
            // The 2328 echo makes the client index its 4-entry bag array with both bag bytes, so both ends are bounded.
            if (!CoordinateOk(e.SrcBag, e.SrcSlot) || !CoordinateOk(e.DstBag, e.DstSlot)) return SwapListRefusal.BadCoordinate;
            if (!owned.TryGetValue(e.Uid, out var row)) return SwapListRefusal.UnknownItem;
            if (!mover.Add(e.Uid)) return SwapListRefusal.DuplicateItem;
            var (flat, storage) = ItemSwapHandler.ClientToPos(e.DstBag, e.DstSlot);
            if (storage != 0 || row.StorageType != 0) return SwapListRefusal.NotBagCell;
            if (!ItemSwapHandler.BagCellExists(flat, rows, lookup)) return SwapListRefusal.CellDoesNotExist;
            if (!landing.Add(flat)) return SwapListRefusal.CellCollision;
            if (row.Slot != flat) planned.Add((row, (short)flat));
        }

        // A row the batch does not move keeps its cell; an entry may not land on it.
        foreach (var r in owned.Values)
            if (r.StorageType == 0 && r.Slot >= 0 && !mover.Contains(r.Id) && landing.Contains(r.Slot))
                return SwapListRefusal.CellCollision;

        moves = planned;
        return SwapListRefusal.None;
    }

    /// <summary>
    /// INV-H12 per entry end: bag 1..4 (bag 0 is not an inventory container for the sort; the client's own sort never
    /// sends it) and a slot inside the container's wire bound. A 1325 entry carries no message tag and no actor handle, so
    /// those checks of <see cref="InventoryRequest.Validate"/> are passed trivially.
    /// </summary>
    internal static bool CoordinateOk(byte bag, byte slot)
        => bag >= 1 && InventoryRequest.Validate(0, 0, 0u, 0u, bag, slot, 1, 1) == InvReject.None;

    /// <summary>
    /// Writes <paramref name="moves"/> all-or-nothing as far as the repository allows. A reshuffle is a permutation, so
    /// writing the final slots one by one would put two rows on one cell under the unique placement index: every moving
    /// row is first parked at the exempt unplaced slot (-1), then settled in its new cell (the 1324 swap's trick, in
    /// bulk). If any write throws, every touched row is parked and put back on its original cell, best effort, and false
    /// is returned.
    /// </summary>
    internal static async Task<bool> PersistAsync(IInventoryRepository inventory,
        IReadOnlyList<(InventoryItem Row, short Flat)> moves, ILogger logger)
    {
        var originals = moves.Select(m => m.Row.Slot).ToArray();
        try
        {
            foreach (var (row, _) in moves) { row.Slot = -1; await inventory.UpdateItemAsync(row); }
            foreach (var (row, flat) in moves) { row.Slot = flat; await inventory.UpdateItemAsync(row); }
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "1325 batch write failed; restoring {Count} rows", moves.Count);
            try
            {
                foreach (var (row, _) in moves) { row.Slot = -1; await inventory.UpdateItemAsync(row); }
                for (int i = 0; i < moves.Count; i++) { moves[i].Row.Slot = originals[i]; await inventory.UpdateItemAsync(moves[i].Row); }
            }
            catch (Exception restoreEx)
            {
                logger.LogError(restoreEx, "1325 batch restore failed; RepairSlotsAsync heals unplaced rows at next login");
            }
            return false;
        }
    }
}
