using Microsoft.Extensions.Logging;
using RO2.Application;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.GameData;
using RO2.Economy.Application;
using RO2.Economy.Domain;
using RO2.Inventory.Application;
using RO2.Inventory.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Host implementation of <see cref="ITwoPartyTradeInventory"/> over the inventory repository and the per-character
/// inventory gate. It holds the exact validate, plan and move steps the trade confirm handler used inline: the whole
/// exchange runs while holding BOTH traders' inventory gates (lowest character id first, see
/// <see cref="CharacterInventoryGate.WithBothGatesAsync{T}"/>), the offered rows are re-validated against the live
/// inventory and the snapshots the window showed, the placement is planned by the pure <see cref="TradePlanner"/>
/// and executed with the same owner-checked repository writes. It moves no zeny and rolls no item back.
/// </summary>
/// <param name="inventory">The inventory repository.</param>
/// <param name="items">Item data: Stack_Max sizes the merge plan, Is_Trade re-runs the tradeable gate, Bag_Size counts cells.</param>
/// <param name="log">The logger the validation warnings go through (the confirm handler's).</param>
public sealed class TradeInventoryAdapter(IInventoryRepository inventory, IReadOnlyDictionary<uint, ItemData> items, ILogger log)
    : ITwoPartyTradeInventory
{
    /// <summary>Inventory <c>StorageType</c> of the character's bag (1 = worn equipment, 2 = bank).</summary>
    private const byte BagStorageType = 0;

    private readonly IInventoryRepository _inventory = inventory;
    private readonly IReadOnlyDictionary<uint, ItemData> _items = items;
    private readonly ILogger _log = log;

    /// <summary>The snapshot of <paramref name="item"/> as offered with <paramref name="qty"/> units.</summary>
    internal static TradeOfferSnapshot SnapshotOf(InventoryItem item, int qty)
        => TradeOfferSnapshot.Of(InventoryEscrowAdapter.ToSnapshot(item), qty);

    /// <inheritdoc />
    /// <remarks>
    /// SP0-INV: validate, plan and move all run while holding BOTH traders' inventory gates (taken in ascending
    /// characterId order so two trades touching the same pair can never deadlock; the gate is non-reentrant, so a
    /// trader with itself takes it once). No concurrent session of either trader can mutate an offered stack between
    /// the ownership check and the move, which is what let a stack be offered here and sold/mailed elsewhere in the
    /// same breath. Every placement passes a cell &gt;= 0, so the repository's <c>AddItemAsync</c> never takes the
    /// gate itself (it does for an unplaced item) and there is no re-entrant deadlock.
    /// </remarks>
    public Task<TradeSwapOutcome> SwapItemsAsync(TradeSwapRequest request)
        => CharacterInventoryGate.WithBothGatesAsync(request.A.CharacterId, request.B.CharacterId, () => SwapUnderGatesAsync(request));

    private async Task<TradeSwapOutcome> SwapUnderGatesAsync(TradeSwapRequest request)
    {
        // ---- VALIDATE (no mutation yet) ----
        // Stack_Max and Is_Trade come from ItemInfo: the first sizes the merge plan below (the same per-template cap
        // ItemSwapHandler's merge uses), the second re-runs the tradeable gate on the live row.
        var myItems = await ResolveOwnedAsync(request.TradeId, request.A);
        var partnerItems = await ResolveOwnedAsync(request.TradeId, request.B);
        if (myItems is null || partnerItems is null)
            return new TradeSwapOutcome(TradeSwapStatus.OwnershipFailed);

        // ---- PLAN (still no mutation) ----
        // Both placements are computed from the CURRENT inventories, before any move, so the two directions cannot
        // collide.
        var myInv = await _inventory.GetByCharacterAsync(request.A.CharacterId);
        var partnerInv = await _inventory.GetByCharacterAsync(request.B.CharacterId);
        var plan = TradePlanner.PlanSwap(SideOf(myItems, myInv), SideOf(partnerItems, partnerInv), StackMax);

        // NO ROOM: refuse the whole trade and move nothing.
        if (plan.AToB is null || plan.BToA is null)
            return new TradeSwapOutcome(TradeSwapStatus.NoRoom) { NoRoomForA = plan.BToA is null, NoRoomForB = plan.AToB is null };

        // ---- APPLY (move, not copy) ----
        // ALL item moves, each one atomic and owner-checked. A throw, or a move the giver's row no longer allows, ends
        // the swap here (E-H3). Items are not rolled back, so a failure after the first move leaves a bounded loss for
        // one side, never a copy.
        var progress = new SwapProgress();
        try
        {
            await ApplyPlacementAsync(plan.AToB, myItems, partnerInv, request.B.CharacterId, request.A.CharacterId, progress);
            await ApplyPlacementAsync(plan.BToA, partnerItems, myInv, request.A.CharacterId, request.B.CharacterId, progress);
        }
        catch (Exception ex)
        {
            return new TradeSwapOutcome(progress.Writes == 0 ? TradeSwapStatus.FaultedBeforeAnyMove : TradeSwapStatus.FaultedMidSwap)
            {
                Fault = ex,
                Writes = progress.Writes,
                NotYetMoved = request.A.Items.Concat(request.B.Items).Select(i => i.Uid)
                    .Where(u => !progress.Settled.Contains(u)).ToList(),
            };
        }

        return new TradeSwapOutcome(TradeSwapStatus.Swapped)
        {
            TemplatesFromA = myItems.Select(m => m.Item.ItemId).ToList(),
            TemplatesFromB = partnerItems.Select(m => m.Item.ItemId).ToList(),
        };
    }

    private int StackMax(uint itemId) => _items.TryGetValue(itemId, out var data) ? data.Stack_Max : 0;

    /// <summary>One side for the planner: its offered live stacks, its bag stacks and the cells a new stack can take.</summary>
    /// <remarks>The cells come from <see cref="BagCapacity.FreeCells"/>: the main bag (flat 0..19), then each equipped
    /// bag up to its ItemInfo <c>Bag_Size</c>, lowest first. The old search walked up from flat 0 with no upper bound, so
    /// a full bag grew past its real cells into bags the player does not have.</remarks>
    private TradePlanSide SideOf(List<(InventoryItem Item, int Qty)> offered, IReadOnlyList<InventoryItem> inventory)
        => new(
            offered.Select(o => new TradePlanItem(o.Item.Id, o.Item.ItemId, o.Item.Count, o.Qty)).ToList(),
            inventory.Where(i => i.StorageType == BagStorageType).Select(i => new BagStack(i.Id, i.ItemId, i.Count, i.Slot)).ToList(),
            BagCapacity.FreeCells(inventory, _items));

    /// <summary>
    /// Resolve each offered uid to a live inventory item that STILL belongs to the offerer and sits in the bag
    /// (StorageType 0), paired with the quantity the window showed for it (the 1353 count, or the whole stack when none
    /// was recorded). Returns null if ANY item fails the check — that aborts the whole trade, so a stale/foreign/moved
    /// item can never be transferred. A stack that now holds FEWER units than were shown also aborts: the partner
    /// agreed to the shown quantity, and silently handing over less is the swindle the lock/confirm round exists to
    /// prevent.
    /// </summary>
    /// <remarks>
    /// E-H4. A uid names a row, and a freed uid is reused, so the row under an offered uid can have become a different
    /// template or a re-refined / re-socketed / re-dyed instance since the partner looked at it. The row is therefore
    /// also compared, field by field, to the <see cref="TradeOfferSnapshot"/> taken when it was accepted into the
    /// window, and the tradeable gate is run again on the live row (it may have become bound). Any difference aborts
    /// the whole trade.
    /// </remarks>
    private async Task<List<(InventoryItem Item, int Qty)>?> ResolveOwnedAsync(Guid tradeId, TradeSwapSide side)
    {
        var result = new List<(InventoryItem Item, int Qty)>(side.Items.Count);
        foreach (var offeredItem in side.Items)
        {
            var uid = offeredItem.Uid;
            var item = await _inventory.GetByIdAsync(uid);
            if (item is null || item.CharacterId != side.CharacterId || item.StorageType != BagStorageType)
                return null;
            int stack = Math.Max(1, item.Count);
            int qty = offeredItem.Qty ?? stack;
            if (qty < 1 || qty > stack)
                return null;
            if (offeredItem.Snapshot is { } offered && offered != SnapshotOf(item, qty))
            {
                _log.LogWarning("Trade {TradeId}: offered uid {Uid} is no longer what was offered ({Offered} -> {Now})",
                    tradeId, uid, offered, SnapshotOf(item, qty));
                return null;
            }
            if (_items.TryGetValue(item.ItemId, out var template) && TradeGate.RefusedAsNotTradeable(template, item))
            {
                _log.LogWarning("Trade {TradeId}: offered uid {Uid} (item {ItemId}) is untradeable or bound now",
                    tradeId, uid, item.ItemId);
                return null;
            }
            result.Add((item, qty));
        }
        return result;
    }

    /// <summary>
    /// Writes a <see cref="TradePlanner"/> placement: each offered item moved, split into its planned cell, or (fully
    /// merged) removed or shrunk, then the merged counts of the recipient's held stacks. Items belong to
    /// <paramref name="fromCharId"/> and move to <paramref name="toCharId"/>. A write the giver's row refuses throws,
    /// so the caller can end the swap; every successful write is counted in <paramref name="progress"/>.
    /// </summary>
    /// <remarks>
    /// SP0-INV. Every write is atomic and owner-checked, so it can neither revert a concurrent change nor
    /// touch a row that is no longer the expected owner's (both gates are held, so under normal operation no
    /// such race exists; this is the cross-process backstop and the proof the move cannot dupe or lose):
    /// <list type="bullet">
    /// <item>a whole stack changing owner (E-H3) is ONE guarded write, <see cref="IInventoryRepository.TryReassignAsync"/>:
    /// the row keeps its uid and is the giver's or the receiver's, never both and never neither;</item>
    /// <item>a giver's partial offer shrinks by a guarded <c>$inc</c>, a fully-merged whole stack is an owner-filtered
    /// delete;</item>
    /// <item>every giver-side write of the batch comes BEFORE any receiver-side increment (a held stack growing by
    /// <see cref="IInventoryRepository.AdjustCountAsync"/>), so a failure part-way loses units, it never copies them.</item>
    /// </list>
    /// RESIDUAL (bounded, loss-not-dupe). Two paths are still two writes: the partial-split (shrink the giver's stack,
    /// then insert the split-off row) and a whole stack whose planned count differs from its row (a partial merge into
    /// the receiver's stacks, or later offers merged into its cell): remove, then insert under the same uid. A throw
    /// between the two writes destroys that item's units; it cannot create any.
    /// </remarks>
    private async Task ApplyPlacementAsync(TradePlacement plan, List<(InventoryItem Item, int Qty)> offered,
        IReadOnlyList<InventoryItem> recipientInventory, uint toCharId, uint fromCharId, SwapProgress progress)
    {
        var live = offered.ToDictionary(o => o.Item.Id, o => o.Item);
        foreach (var move in plan.Moves)
        {
            var it = live[move.Item.Uid];
            int qty = move.Item.Qty;
            bool wholeStack = move.WholeStack;
            if (move.Cell < 0)
            {
                // Everything merged. The offered uid disappears when the whole stack went;
                // on a partial offer the giver just keeps the rest under its uid.
                bool taken = wholeStack
                    ? await _inventory.RemoveItemAsync(it.Id, fromCharId)
                    : await _inventory.AdjustCountAsync(it.Id, fromCharId, -qty) is not null;
                if (!taken) throw GiverNoLongerHolds(it);
                progress.Settle(it.Id);
                continue;
            }

            if (!wholeStack)
            {
                // Shrink the giver's stack by the offered amount (guarded $inc), then create the split-off
                // part as a new row for the receiver. The new row takes the giver's per-instance state through
                // the one copier of it: listing Refine/Durability/CardsJson here dropped a bestowed element
                // (record+68). It takes a fresh uid (Id 0), so the two directions can never collide on one uid.
                if (await _inventory.AdjustCountAsync(it.Id, fromCharId, -qty) is null)
                    throw GiverNoLongerHolds(it);   // the giver's stack is gone (cannot happen under the gates)
                progress.Settle(it.Id);
                await _inventory.AddItemAsync(new InventoryItem
                {
                    CharacterId = toCharId,
                    ItemId = it.ItemId,
                    Count = move.CellCount,
                    Slot = move.Cell,
                    StorageType = 0,
                }.CopyInstanceStateFrom(it));
                progress.Wrote();
            }
            else if (move.CellCount == it.Count)
            {
                // Owner change, the common case: the whole row moves to its planned cell in one guarded write, so it
                // is never in two bags and never in none (E-H3).
                if (!await _inventory.TryReassignAsync(it.Id, fromCharId, toCharId, 0, move.Cell))
                    throw GiverNoLongerHolds(it);
                progress.Settle(it.Id);
            }
            else
            {
                // Owner change of a row whose planned count differs from its stored one: take it off the giver FIRST
                // (owner-filtered), then insert it for the receiver under the SAME uid with the planned count. See the
                // RESIDUAL note above.
                if (!await _inventory.RemoveItemAsync(it.Id, fromCharId))
                    throw GiverNoLongerHolds(it);
                progress.Settle(it.Id);
                await _inventory.AddItemAsync(new InventoryItem
                {
                    Id = it.Id,
                    CharacterId = toCharId,
                    ItemId = it.ItemId,
                    Count = move.CellCount,
                    Slot = move.Cell,
                    StorageType = 0,
                }.CopyInstanceStateFrom(it));
                progress.Wrote();
            }
        }

        foreach (var (heldUid, count) in plan.HeldCounts)
        {
            // The recipient's own held stack grows by the merged-in units: an atomic $inc, not a stale
            // whole-row write that could clobber a concurrent change to this stack. It comes after every giver-side
            // write above, so units are never in both places.
            var held = recipientInventory.First(i => i.Id == heldUid);
            var merged = await _inventory.AdjustCountAsync(held.Id, toCharId, count - held.Count);
            held.Count = merged ?? count;
            progress.Wrote();
        }
    }

    private static InvalidOperationException GiverNoLongerHolds(InventoryItem item) =>
        new($"Trade move of uid {item.Id:X} refused: the giver's row is gone or no longer theirs");

    /// <summary>How far a swap's item phase got: how many writes landed and which offered uids were taken off
    /// their giver (the rest are what a mid-swap failure still has to reconcile).</summary>
    private sealed class SwapProgress
    {
        public int Writes { get; private set; }
        public HashSet<ulong> Settled { get; } = [];

        public void Wrote() => Writes++;

        public void Settle(ulong uid)
        {
            Settled.Add(uid);
            Writes++;
        }
    }
}
