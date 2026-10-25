using Microsoft.Extensions.Logging;
using RO2.Application;
using RO2.Application.Interfaces;
using RO2.BuildingBlocks;
using RO2.Domain.Entities;
using RO2.GameData;
using RO2.Economy.Application;
using RO2.Economy.Domain;
using RO2.Inventory.Application;
using RO2.Inventory.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Host implementation of <see cref="IShopInventory"/> over the inventory repository and the per-character inventory
/// gate. It holds the exact read/write steps the NPC-shop handlers used inline: the sale loop runs under ONE hold of
/// the seller's gate with owner-checked atomic writes, the buy-back's cell pick and add run under the buyer's gate,
/// and a buy's writes are fail-closed with an undo stack. It maps between the game's inventory row and the neutral
/// <see cref="ItemStackSnapshot"/> the economy flows carry.
/// </summary>
/// <param name="inventory">The inventory repository.</param>
/// <param name="items">Item data, used for each equipped bag's Bag_Size when counting free cells.</param>
/// <param name="log">The logger the undo of a failed buy reports through.</param>
public sealed class ShopInventoryAdapter(IInventoryRepository inventory, IReadOnlyDictionary<uint, ItemData> items, ILogger log)
    : IShopInventory
{
    /// <summary>Inventory <c>StorageType</c> of the character's bag (1 = worn equipment, 2 = bank).</summary>
    private const byte BagStorageType = 0;

    private readonly IInventoryRepository _inventory = inventory;
    private readonly IReadOnlyDictionary<uint, ItemData> _items = items;
    private readonly ILogger _log = log;

    /// <summary>A bag read: the neutral stacks the planner sees and, behind them, the live rows the write mutates.</summary>
    internal sealed class BagView(IReadOnlyList<InventoryItem> owned, IReadOnlyDictionary<uint, ItemData> items) : IShopBag
    {
        private IReadOnlyList<short>? _free;

        /// <summary>Every row the character owns, of any storage type (the rows a write mutates).</summary>
        public IReadOnlyList<InventoryItem> Owned { get; } = owned;

        /// <inheritdoc />
        public IReadOnlyList<BagStack> Stacks { get; } = owned.Where(i => i.StorageType == BagStorageType)
            .Select(i => new BagStack(i.Id, i.ItemId, i.Count, i.Slot)).ToList();

        /// <inheritdoc />
        /// <remarks>The cells come from BagCapacity rather than from AddItemAsync's Slot -1, which takes the lowest
        /// unused index with no upper bound: a full main bag and a 30-cell bag in character-bag cell 0 would put the
        /// next stack in flat 50, a cell no bag provides.</remarks>
        public IReadOnlyList<short> FreeCells => _free ??= BagCapacity.FreeCells(Owned, items);
    }

    /// <summary>Maps already-read rows to the bag view the planner and the write use.</summary>
    internal static BagView BagOf(IReadOnlyList<InventoryItem> owned, IReadOnlyDictionary<uint, ItemData> items)
        => new(owned, items);

    /// <inheritdoc />
    public async Task<IShopBag> ReadBagAsync(uint characterId)
        => BagOf(await _inventory.GetByCharacterAsync(characterId), _items);

    /// <inheritdoc />
    public async Task<ShopBuyWrite> WriteBuyAsync(IShopBag bag, uint characterId, uint itemId, int quantity, ShopBuyPlan plan,
        uint templateDurability, IReadOnlyList<StackTake>? tokenTakes)
    {
        var owned = ((BagView)bag).Owned;
        var stacks = new List<InventoryItem>();
        var touches = new List<ShopTokenTouch>();
        var undo = new List<Func<Task>>();
        try
        {
            if (plan.RestackId is { } restackId)
            {
                // The live row of the bag read: the 2375 record carries its existing uid with the new total.
                var target = owned.First(i => i.Id == restackId);
                var before = target.Count;
                undo.Add(async () =>
                {
                    target.Count = before;
                    await _inventory.UpdateItemAsync(target);
                });
                target.Count += quantity;
                await _inventory.UpdateItemAsync(target);
                stacks.Add(target);
            }
            else
            {
                short durability = (short)Math.Min(templateDurability, (uint)short.MaxValue);
                foreach (var (cell, count) in plan.NewStacks)
                {
                    var stack = new InventoryItem
                    {
                        CharacterId = characterId,
                        ItemId = itemId,
                        Count = count,
                        Slot = cell,
                        StorageType = BagStorageType,
                        Durability = durability,
                        // record+69 = 0, not the template's Abili_val: a bought stack's 2375 record carries 0 there
                        // (see WorldEntryService.RecordAbilityValue).
                        AbilityValue = 0,
                    };
                    undo.Add(async () =>
                    {
                        if (stack.Id != 0) await _inventory.RemoveItemAsync(stack.Id);   // 0: never stamped, never inserted
                    });
                    await _inventory.AddItemAsync(stack);   // stamps the instance uid; a cell >= 0 is kept as given
                    stacks.Add(stack);
                }
            }

            // The tokens, after the bought stacks are stored (nothing is taken for a buy that could not be delivered).
            foreach (var (stackId, left) in tokenTakes ?? [])
            {
                var stack = owned.First(i => i.Id == stackId);
                var original = stack.Count;
                if (left <= 0)
                {
                    undo.Add(async () =>
                    {
                        if (await _inventory.GetByIdAsync(stack.Id) is null) await _inventory.AddItemAsync(stack);
                    });
                    var snapshot = InventoryEscrowAdapter.ToSnapshot(stack);   // as it stood before the removal
                    await _inventory.RemoveItemAsync(stack.Id);
                    touches.Add(new ShopTokenTouch(snapshot, Emptied: true));
                }
                else
                {
                    undo.Add(async () =>
                    {
                        stack.Count = original;
                        await _inventory.UpdateItemAsync(stack);
                    });
                    stack.Count = left;
                    await _inventory.UpdateItemAsync(stack);
                    touches.Add(new ShopTokenTouch(InventoryEscrowAdapter.ToSnapshot(stack), Emptied: false));
                }
            }
            return new ShopBuyWrite(stacks.Select(InventoryEscrowAdapter.ToSnapshot).ToList(), touches);
        }
        catch
        {
            for (var i = undo.Count - 1; i >= 0; i--)
            {
                try { await undo[i](); }
                catch (Exception undoEx)
                {
                    _log.LogError(undoEx, "Buy of item {Item} for character {Char}: could not undo write {Step} after a failed buy; "
                        + "the bag may hold an item without its tokens taken", itemId, characterId, i);
                }
            }
            throw;
        }
    }

    /// <inheritdoc />
    public Task TakeForSaleAsync(uint characterId, IReadOnlyList<ShopSellEntry> entries, Func<ItemStackSnapshot, bool> isSellable,
        Action<ShopSaleLine> onSold)
    {
        // The whole load-decide-write loop runs under this character's gate, so a second concurrent session (another
        // sell, a trade-away, a mail attach) cannot interleave between the read and the write of a stack and get it
        // paid for twice. Each per-item write is also atomic and owner-checked, which is the cross-process backstop
        // the in-process gate cannot give.
        return CharacterInventoryGate.RunAsync(characterId, async () =>
        {
            foreach (var (itemUid, qty) in entries)
            {
                var it = await _inventory.GetByIdAsync(itemUid);
                // Ownership guard: uids are global, only the owner may sell the item.
                if (it is null || it.CharacterId != characterId) continue;
                // Only the bag can be sold: an equipped or banked uid is owned too, and without this it would be paid
                // for and deleted from where it is worn or stored.
                if (it.StorageType != BagStorageType) continue;
                // The template must allow a sale. An unknown template is skipped too: its price and flag are not known.
                if (!isSellable(InventoryEscrowAdapter.ToSnapshot(it))) continue;
                // Gold-dupe guard: NEVER pay for more than the owned stack. The client sends a u8 count (1..255);
                // without this clamp, selling qty > it.Count still credits unit*qty while the stack is only removed
                // once.
                var sellQty = ShopPricing.SellQty(qty, it.Count);
                if (sellQty <= 0) continue;
                var newCount = it.Count - sellQty;

                // Take the stack atomically and owner-checked BEFORE crediting: a whole-stack sale is an
                // owner-filtered delete, a partial one an $inc guarded by the live count. If the guard fails the row
                // no longer holds what we would pay out (a concurrent session already removed or shrank it), so
                // nothing is credited, recorded or put on the buy-back list, and no gold is minted.
                var removed = newCount <= 0
                    ? await _inventory.RemoveItemAsync(itemUid, characterId)
                    : (await _inventory.AdjustCountAsync(itemUid, characterId, -sellQty)).HasValue;
                if (!removed) continue;

                // The stack as it was (its pre-sale count): the record is built from it, then its count byte forced.
                var row = InventoryEscrowAdapter.ToSnapshot(it);
                if (newCount > 0) it.Count = newCount;
                onSold(new ShopSaleLine(row, sellQty, newCount));
            }
            return 0;
        });
    }

    /// <inheritdoc />
    public Task<ItemStackSnapshot?> PlaceBackAsync(uint characterId, ItemStackSnapshot sold)
    {
        // The cell comes from BagCapacity, not from AddItemAsync's Slot -1, which takes the lowest unused index with no
        // upper bound (a full bag would put the item in a cell no bag provides). The pick and the add run under the
        // character's inventory gate so a concurrent add cannot take the same cell between them.
        return CharacterInventoryGate.RunAsync<ItemStackSnapshot?>(characterId, async () =>
        {
            var restored = InventoryEscrowAdapter.ToInventoryItem(sold, characterId);   // the same instance back
            var free = BagCapacity.FreeCells(await _inventory.GetByCharacterAsync(characterId), _items);
            if (free.Count == 0) return null;
            restored.Slot = free[0];

            // Retail gives back the same uid. This server's uids come from max(id)+1, so the id of a deleted stack can
            // have been handed out again, and a partly sold stack still has it. Only then does the item get a new uid;
            // the 2377 +144 still echoes the requested one, which is the uid the client's buy-back entry is keyed by.
            if (await _inventory.GetByIdAsync(restored.Id) is not null)
                restored.Id = 0;
            await _inventory.AddItemAsync(restored);
            return InventoryEscrowAdapter.ToSnapshot(restored);
        });
    }
}
