using RO2.Economy.Domain;

namespace RO2.Economy.Application;

/// <summary>A picked listing that can still be sold, with the facts the buy planner needs about the listed item.</summary>
/// <param name="Listing">The listing as picked.</param>
/// <param name="ItemId">The listed item's template id.</param>
/// <param name="HeldCount">The count the listed instance holds in the seller's bag now (at least the listed count).</param>
/// <param name="StackMax">The template's Stack_Max (0 or 1 = not stackable).</param>
public sealed record StallLiveListing(StallListing Listing, uint ItemId, int HeldCount, int StackMax);

/// <summary>The buyer's bag as the stall buy planner sees it.</summary>
/// <param name="Stacks">The buyer's bag stacks (worn and banked rows are not here).</param>
/// <param name="FreeCells">The free bag cells, in the order a new stack takes them.</param>
public sealed record StallBuyerBag(IReadOnlyList<BagStack> Stacks, IReadOnlyList<short> FreeCells);

/// <summary>
/// The inventory and listing-book operations of a stall buy. The host implements it over its inventory repository and
/// its stall registry. One instance serves ONE buy: it keeps the live rows it read, so
/// <see cref="ApplyAsync"/> mutates the very rows the plan was made on. It takes NO inventory gate: the stall's own
/// purchase gate (held by the host around the whole buy) is the only serialization the flow has, as before the carve.
/// It does not roll anything back when a write fails.
/// </summary>
public interface IStallBuyInventory
{
    /// <summary>
    /// Reads the seller's bag and returns the picked listings that can still be sold, in pick order: the listed instance
    /// is still in the bag and holds at least the listed count. Throws when the bag cannot be read.
    /// </summary>
    /// <param name="sellerCharacterId">The seller.</param>
    /// <param name="picked">The listings the buyer picked, in pick order, without repeats.</param>
    Task<IReadOnlyList<StallLiveListing>> ResolveLiveAsync(uint sellerCharacterId, IReadOnlyList<StallListing> picked);

    /// <summary>Reads the buyer's bag for planning where the bought items land. Throws when the bag cannot be read.</summary>
    /// <param name="buyerCharacterId">The buyer.</param>
    Task<StallBuyerBag> ReadBuyerBagAsync(uint buyerCharacterId);

    /// <summary>
    /// Writes a planned placement: first the held buyer stacks that grow, then each bought listing moves (a whole stack
    /// keeps its uid, a partial one splits). NOT fail-closed: a write that throws leaves the writes already made in place
    /// and the exception propagates.
    /// </summary>
    /// <param name="buyerCharacterId">The buyer.</param>
    /// <param name="sold">The bought listings as <see cref="ResolveLiveAsync"/> returned them.</param>
    /// <param name="plan">Where the quantities land: one move per entry of <paramref name="sold"/>, in order.</param>
    Task ApplyAsync(uint buyerCharacterId, IReadOnlyList<StallLiveListing> sold, TradePlacement plan);

    /// <summary>Removes the sold listings from the stall's listing book.</summary>
    /// <param name="indices">The list indices of the sold listings.</param>
    void RemoveListings(IReadOnlyList<byte> indices);
}
