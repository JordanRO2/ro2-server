using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.GameData;
using RO2.Economy.Application;
using RO2.Economy.Domain;
using RO2.Inventory.Domain;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Host implementation of <see cref="IStallBuyInventory"/> over the inventory repository and the stall registry. It holds
/// the exact read, placement-write and listing-removal steps the private-shop buy handler used inline. It takes NO
/// gate itself: the handler holds the stall's purchase gate and, around the whole buy, both characters' inventory gates.
/// Its writes are owner-guarded and atomic, and it rolls nothing back when one throws. One instance serves one buy: it keeps the live seller and buyer rows it read, and, after
/// <see cref="ApplyAsync"/>, the buyer stacks that received items and the seller's sale records the handler's frames need.
/// </summary>
/// <param name="inventory">The inventory repository.</param>
/// <param name="items">Item data: Stack_Max sizes the merge plan, Bag_Size counts the buyer's free cells.</param>
/// <param name="owner">The stall owner, whose registry entry loses the sold listings.</param>
internal sealed class StallBuyInventoryAdapter(IInventoryRepository inventory, IReadOnlyDictionary<uint, ItemData> items, PlayerState owner)
    : IStallBuyInventory
{
    private readonly IInventoryRepository _inventory = inventory;
    private readonly IReadOnlyDictionary<uint, ItemData> _items = items;
    private readonly PlayerState _owner = owner;

    private List<InventoryItem> _soldRows = [];
    private readonly Dictionary<ulong, InventoryItem> _buyerHeld = new();
    private readonly Dictionary<InventoryItem, byte[]> _leavingWhole = new(ReferenceEqualityComparer.Instance);

    /// <summary>After <see cref="ApplyAsync"/>: the buyer's bag stacks that received items, as they now stand, in the order
    /// they were first filled (one 2333 each).</summary>
    public List<InventoryItem> BuyerStacks { get; private set; } = [];

    /// <summary>The seller's sold stacks as they now stand, as 126-byte records in listing order (one 2379 each). Built on
    /// demand, after <see cref="ApplyAsync"/>.</summary>
    public List<byte[]> BuildSellerRecords() => _soldRows.Select(row => _leavingWhole.TryGetValue(row, out var gone)
        ? gone : ItemPackets.BuildItemRecord126(row)).ToList();

    /// <inheritdoc />
    public async Task<IReadOnlyList<StallLiveListing>> ResolveLiveAsync(uint sellerCharacterId, IReadOnlyList<StallListing> picked)
    {
        var live = PrivateShopRegistry.Live(
            picked.Select(l => new PrivateShopRegistry.Listing(l.Index, l.Uid, l.Count, l.Price)),
            await _inventory.GetByCharacterAsync(sellerCharacterId));
        _soldRows = live.Select(l => l.Item).ToList();
        return live.Select(l => new StallLiveListing(
            new StallListing(l.Listing.Index, l.Listing.Uid, l.Listing.Count, l.Listing.Price),
            l.Item.ItemId, l.Item.Count, _items.TryGetValue(l.Item.ItemId, out var data) ? data.Stack_Max : 0)).ToList();
    }

    /// <inheritdoc />
    public async Task<StallBuyerBag> ReadBuyerBagAsync(uint buyerCharacterId)
    {
        var all = await _inventory.GetByCharacterAsync(buyerCharacterId);
        var freeCells = BagCapacity.FreeCells(all, _items);
        _buyerHeld.Clear();
        foreach (var row in all.Where(i => i.StorageType == 0 && i.Slot >= 0)) _buyerHeld[row.Id] = row;
        var stacks = _buyerHeld.Values.Select(i => new BagStack(i.Id, i.ItemId, i.Count, i.Slot)).ToList();
        return new StallBuyerBag(stacks, freeCells);
    }

    /// <inheritdoc />
    public async Task ApplyAsync(uint buyerCharacterId, IReadOnlyList<StallLiveListing> sold, TradePlacement plan)
    {
        // The records of the stacks that leave the seller whole, taken before anything is written.
        for (int i = 0; i < sold.Count; i++)
        {
            var row = _soldRows[i];
            if (sold[i].Listing.Count >= row.Count)
            {
                var record = ItemPackets.BuildItemRecord126(row);
                record[14] = 0;
                _leavingWhole[row] = record;
            }
        }

        // Every write is owner-guarded and atomic (the caller holds both characters' inventory gates around the whole buy,
        // so the rows read above are current; the guards are the cross-process backstop). The seller's side comes first,
        // the buyer's held stacks grow last, so a failure part-way loses units and never copies them.
        uint sellerId = _owner.CharacterId;
        var placed = new InventoryItem?[plan.Moves.Count];
        for (int i = 0; i < plan.Moves.Count; i++)
        {
            var move = plan.Moves[i];
            var it = _soldRows[i];
            bool wholeStack = move.Item.Qty >= it.Count;
            if (move.Cell < 0)
            {
                if (wholeStack)
                {
                    if (!await _inventory.RemoveItemAsync(it.Id, sellerId)) throw SellerNoLongerHolds(it);
                }
                else it.Count = await _inventory.AdjustCountAsync(it.Id, sellerId, -move.Item.Qty) ?? throw SellerNoLongerHolds(it);
                continue;
            }
            if (!wholeStack)
            {
                it.Count = await _inventory.AdjustCountAsync(it.Id, sellerId, -move.Item.Qty) ?? throw SellerNoLongerHolds(it);
                var row = new InventoryItem
                {
                    CharacterId = buyerCharacterId,
                    ItemId = it.ItemId,
                    Count = move.CellCount,
                    Slot = move.Cell,
                    StorageType = 0,
                }.CopyInstanceStateFrom(it);
                await _inventory.AddItemAsync(row);
                placed[i] = row;
            }
            else
            {
                if (!await _inventory.TryReassignAsync(it.Id, sellerId, buyerCharacterId, 0, move.Cell)) throw SellerNoLongerHolds(it);
                if (move.CellCount != it.Count
                    && await _inventory.AdjustCountAsync(it.Id, buyerCharacterId, move.CellCount - it.Count) is null)
                    throw SellerNoLongerHolds(it);
                it.CharacterId = buyerCharacterId;
                it.StorageType = 0;
                it.Slot = move.Cell;
                it.Count = move.CellCount;
                placed[i] = it;
            }
        }

        foreach (var (uid, count) in plan.HeldCounts)
        {
            var held = _buyerHeld[uid];
            held.Count = await _inventory.AdjustCountAsync(held.Id, buyerCharacterId, count - held.Count)
                ?? throw new InvalidOperationException($"Stall buy: the buyer's stack {held.Id:X} is gone from their bag");
        }

        BuyerStacks = plan.Filled.Select(f => f.HeldUid is { } uid ? _buyerHeld[uid] : placed[f.MoveIndex]!).ToList();
    }

    private static InvalidOperationException SellerNoLongerHolds(InventoryItem item) =>
        new($"Stall buy of uid {item.Id:X} refused: the seller's row is gone or no longer theirs");

    /// <inheritdoc />
    public void RemoveListings(IReadOnlyList<byte> indices)
    {
        foreach (var index in indices) PrivateShopRegistry.TryRemove(_owner, index);
    }
}
