using RO2.BuildingBlocks;
using RO2.Economy.Domain;
using RO2.Inventory.Domain;

namespace RO2.Economy.Application;

/// <summary>
/// A read of a character's bag as the shop planner sees it: the bag stacks and the free cells a new stack can land
/// in. The host implementation also keeps the live rows it read, so <see cref="IShopInventory.WriteBuyAsync"/> mutates
/// the very rows the plan was made on; hand it back only to the port that produced it.
/// </summary>
public interface IShopBag
{
    /// <summary>The bag stacks (worn and banked rows are not here).</summary>
    IReadOnlyList<BagStack> Stacks { get; }

    /// <summary>The free bag cells, in the order a new stack takes them.</summary>
    IReadOnlyList<short> FreeCells { get; }
}

/// <summary>A token stack a token buy touched: <see cref="Stack"/> is the stack as it now stands (its new count), or
/// as it stood before it was emptied when <see cref="Emptied"/> is set.</summary>
/// <param name="Stack">The token stack: after the take, or before the removal when emptied.</param>
/// <param name="Emptied">True when the take emptied the stack and its row was removed.</param>
public sealed record ShopTokenTouch(ItemStackSnapshot Stack, bool Emptied);

/// <summary>What a buy wrote: the bought stacks as they now stand (a restack carries its existing uid and the new
/// total) and, for a token buy, each token stack touched, in the order they were taken.</summary>
/// <param name="Stacks">The bought stacks.</param>
/// <param name="TokenTouches">The token stacks the buy took from.</param>
public sealed record ShopBuyWrite(IReadOnlyList<ItemStackSnapshot> Stacks, IReadOnlyList<ShopTokenTouch> TokenTouches);

/// <summary>One requested sale: the stack's uid and the quantity the client asked for.</summary>
/// <param name="Uid">The stack's item instance uid.</param>
/// <param name="Qty">The requested quantity (at least 1).</param>
public sealed record ShopSellEntry(ulong Uid, int Qty);

/// <summary>One stack the port took for a sale. <see cref="Row"/> is the stack as it was read, before the sale.</summary>
/// <param name="Row">The stack before the sale (its pre-sale count).</param>
/// <param name="SellQty">The quantity taken: never more than the stack held.</param>
/// <param name="NewCount">The count left in the bag; 0 or less when the whole stack was sold.</param>
public sealed record ShopSaleLine(ItemStackSnapshot Row, int SellQty, int NewCount);

/// <summary>
/// The inventory operations the NPC-shop flows need, atomic under the per-character inventory gate where the
/// original flows were. The host implements it over its inventory repository; the economy application depends only on
/// this contract, never on the game's inventory row type.
/// </summary>
public interface IShopInventory
{
    /// <summary>Reads the character's bag for planning a buy. Throws when the bag cannot be read.</summary>
    Task<IShopBag> ReadBagAsync(uint characterId);

    /// <summary>
    /// Writes a planned buy: delivers the bought stacks (the plan's restack or new stacks), then takes the tokens.
    /// FAIL-CLOSED: when any write throws, every write already made is undone, newest first, and the original
    /// exception is rethrown, so nothing is left delivered without its tokens taken. A new stack carries the
    /// template durability (capped at <see cref="short.MaxValue"/>) and a zero ability value.
    /// </summary>
    /// <param name="bag">The bag <see cref="ReadBagAsync"/> returned and the plan was made on.</param>
    /// <param name="characterId">The buyer.</param>
    /// <param name="itemId">The bought template.</param>
    /// <param name="quantity">The bought quantity.</param>
    /// <param name="plan">Where the quantity lands.</param>
    /// <param name="templateDurability">The template's durability, used for new stacks.</param>
    /// <param name="tokenTakes">The token stacks to take from and the count each is left with; null for a zeny buy.</param>
    Task<ShopBuyWrite> WriteBuyAsync(IShopBag bag, uint characterId, uint itemId, int quantity, ShopBuyPlan plan,
        uint templateDurability, IReadOnlyList<StackTake>? tokenTakes);

    /// <summary>
    /// Takes the requested stacks out of the bag for a sale, the whole load-decide-write loop under ONE hold of the
    /// character's inventory gate, so a concurrent session cannot interleave between the read and the write of a
    /// stack and get it paid for twice. A stack is skipped (not an error) when it is gone, not the character's, not in
    /// the bag, rejected by <paramref name="isSellable"/>, or no longer holds what the sale would pay out. The take is
    /// atomic and owner-checked; <paramref name="onSold"/> runs after each successful take, inside the gate.
    /// </summary>
    /// <param name="characterId">The seller.</param>
    /// <param name="entries">The requested stacks, in request order.</param>
    /// <param name="isSellable">Whether a stack's template allows a sale.</param>
    /// <param name="onSold">Called once per stack taken.</param>
    Task TakeForSaleAsync(uint characterId, IReadOnlyList<ShopSellEntry> entries, Func<ItemStackSnapshot, bool> isSellable,
        Action<ShopSaleLine> onSold);

    /// <summary>
    /// Puts a bought-back stack in the bag's first free cell, the pick and the add under the character's inventory
    /// gate. Returns the stack as stored, or null when the bag has no free cell. The stack keeps its uid unless that
    /// uid is taken meanwhile, in which case it is stored under a new one. Throws when the write fails.
    /// </summary>
    /// <param name="characterId">The buyer.</param>
    /// <param name="sold">The stack to give back.</param>
    Task<ItemStackSnapshot?> PlaceBackAsync(uint characterId, ItemStackSnapshot sold);
}
