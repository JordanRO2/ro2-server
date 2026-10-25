namespace RO2.Economy.Domain;

/// <summary>One offered item as the trade planner sees it: the live stack it comes from and the quantity offered.</summary>
/// <param name="Uid">The offered stack's instance uid.</param>
/// <param name="ItemId">The template id.</param>
/// <param name="Count">The count the live stack holds.</param>
/// <param name="Qty">The quantity offered (1 to <paramref name="Count"/>).</param>
public readonly record struct TradePlanItem(ulong Uid, uint ItemId, int Count, int Qty);

/// <summary>
/// One side of a swap for the planner: what it offers, the bag stacks it holds and the free cells a new stack of
/// the other side's goods can take.
/// </summary>
/// <param name="Offered">The items this side hands over, in offer order.</param>
/// <param name="Bag">This side's bag stacks (worn and banked rows are not here).</param>
/// <param name="FreeCells">This side's free bag cells, in the order a new stack takes them.</param>
public sealed record TradePlanSide(
    IReadOnlyList<TradePlanItem> Offered, IReadOnlyList<BagStack> Bag, IReadOnlyList<short> FreeCells);

/// <summary>An offered item and its destination. <see cref="Cell"/> -1 means it merged completely.</summary>
public sealed class TradePlannedMove
{
    /// <summary>Creates the move of one offered item.</summary>
    /// <param name="item">The offered item.</param>
    public TradePlannedMove(TradePlanItem item) => Item = item;

    /// <summary>The offered item.</summary>
    public TradePlanItem Item { get; }

    /// <summary>The recipient's cell the item ends in, or -1 when it merged completely into held stacks.</summary>
    public short Cell { get; set; } = -1;

    /// <summary>The count that ends in <see cref="Cell"/>, including later offers merged into it.</summary>
    public int CellCount { get; set; }

    /// <summary>True when the whole live stack is offered, not a part of it.</summary>
    public bool WholeStack => Item.Qty >= Item.Count;
}

/// <summary>One side's planned placement: merged counts for held stacks, and one move per offered item.</summary>
public sealed class TradePlacement
{
    /// <summary>
    /// The recipient's held stacks that grow, uid to the PLANNED total count, in the order they were first
    /// merged into.
    /// </summary>
    public Dictionary<ulong, int> HeldCounts { get; } = new();

    /// <summary>One move per offered item, in offer order.</summary>
    public List<TradePlannedMove> Moves { get; } = new();

    /// <summary>The stacks the plan filled (held stacks and planned cells), in the order they were first filled.</summary>
    public List<TradeFill> Filled { get; } = new();
}

/// <summary>One stack a placement filled: a held stack of the recipient (<see cref="HeldUid"/>), or the new cell of the
/// move at <see cref="MoveIndex"/> (<see cref="HeldUid"/> null).</summary>
/// <param name="HeldUid">The held stack's uid; null when the fill is a planned cell.</param>
/// <param name="MoveIndex">With a null <paramref name="HeldUid"/>: the index into <see cref="TradePlacement.Moves"/> of the move whose cell it is.</param>
public readonly record struct TradeFill(ulong? HeldUid, int MoveIndex);

/// <summary>Both directions of a swap. A null placement means that recipient's bag cannot take the goods.</summary>
/// <param name="AToB">Side A's items into side B's bag; null when B has no room.</param>
/// <param name="BToA">Side B's items into side A's bag; null when A has no room.</param>
public sealed record TradeSwapPlan(TradePlacement? AToB, TradePlacement? BToA);

/// <summary>
/// Pure placement planning for a player trade over neutral bag stacks: where each offered item goes in the
/// recipient's bag, computed without writing anything, for both directions of the swap. It knows nothing of the
/// inventory row type or the game data; the host maps its rows in and applies the plan.
/// </summary>
public static class TradePlanner
{
    /// <summary>
    /// Plans both directions of a swap from the CURRENT bags, before any move, so the two directions cannot
    /// collide. Each side's own offered uids are the "leaving" uids of its bag, so the other direction never merges
    /// into a stack that is itself being handed over (or, for a partial offer, shrunk) in this same swap.
    /// </summary>
    /// <param name="a">Side A (the confirmer).</param>
    /// <param name="b">Side B (the partner).</param>
    /// <param name="stackMax">The template's Stack_Max (0 or 1 = not stackable).</param>
    public static TradeSwapPlan PlanSwap(TradePlanSide a, TradePlanSide b, Func<uint, int> stackMax)
        => new(
            PlanPlacement(a.Offered, b.Bag, b.FreeCells, b.Offered.Select(i => i.Uid).ToHashSet(), stackMax),
            PlanPlacement(b.Offered, a.Bag, a.FreeCells, a.Offered.Select(i => i.Uid).ToHashSet(), stackMax));

    /// <summary>
    /// Where each offered item would go in the recipient's bag: the stack merges first, then a free cell for
    /// whatever does not merge. Returns null when some item needs a cell and none is left, which refuses the
    /// whole trade.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A cell held by one of the recipient's OWN offered items is not counted as free, although that item leaves
    /// in the same swap: the two directions are applied one after the other and each takes cells from its
    /// recipient's pre-trade bag.
    /// </para>
    /// <para>
    /// A whole stack is a MOVE: the item keeps its uid. A partial offer splits the stack: the giver keeps the uid
    /// with the remainder, the receiver gets a fresh uid carrying the offered count.
    /// </para>
    /// <para>
    /// STACK MERGE. A stackable item (Stack_Max &gt; 1) is first poured into the recipient's bag stacks of the same
    /// template, each up to Stack_Max, and only what does not fit takes a cell of its own. How an overflow past
    /// Stack_Max is placed is not specified by the protocol; it goes to a new cell rather than being dropped, so
    /// the receiver always gets the full count.
    /// </para>
    /// <para>
    /// <paramref name="leavingUids"/> are the recipient's own offered uids. They are never merge targets.
    /// </para>
    /// </remarks>
    /// <param name="items">The items offered to this recipient, in offer order.</param>
    /// <param name="recipientBag">The recipient's bag stacks.</param>
    /// <param name="freeCells">The recipient's free cells, in the order a new stack takes them.</param>
    /// <param name="leavingUids">The recipient's own offered uids.</param>
    /// <param name="stackMax">The template's Stack_Max (0 or 1 = not stackable).</param>
    public static TradePlacement? PlanPlacement(
        IReadOnlyList<TradePlanItem> items, IReadOnlyList<BagStack> recipientBag, IReadOnlyList<short> freeCells,
        IReadOnlyCollection<ulong> leavingUids, Func<uint, int> stackMax)
    {
        var plan = new TradePlacement();
        int nextFree = 0;

        // Bag stacks the incoming items may merge into, in cell order, with their PLANNED counts (the
        // rows are not touched until the plan is applied). Cells planned below join the list, so a
        // second offered stack of the same template fills the first before opening another cell.
        var stacks = recipientBag
            .Where(i => i.Slot >= 0 && !leavingUids.Contains(i.Id))
            .OrderBy(i => i.Slot)
            .Select(i => new MergeTarget(i.ItemId, i.Count) { HeldUid = i.Id })
            .ToList();

        foreach (var it in items)
        {
            int max = stackMax(it.ItemId);
            int remaining = it.Qty;
            if (max > 1)
            {
                foreach (var target in stacks)
                {
                    if (remaining == 0) break;
                    if (target.ItemId != it.ItemId || target.Count >= max) continue;
                    int add = Math.Min(max - target.Count, remaining);
                    target.Count += add;
                    remaining -= add;
                    if (target.HeldUid is { } held)
                    {
                        if (!plan.HeldCounts.ContainsKey(held)) plan.Filled.Add(new TradeFill(held, -1));
                        plan.HeldCounts[held] = target.Count;
                    }
                    else target.Created!.CellCount = target.Count;
                }
            }

            var move = new TradePlannedMove(it);
            if (remaining > 0)
            {
                if (nextFree >= freeCells.Count)
                    return null; // this item needs a cell and the bag has none left
                move.Cell = freeCells[nextFree++];
                move.CellCount = remaining;
                stacks.Add(new MergeTarget(it.ItemId, remaining) { Created = move });
                plan.Filled.Add(new TradeFill(null, plan.Moves.Count));
            }
            plan.Moves.Add(move);
        }
        return plan;
    }

    /// <summary>A stack an incoming item may merge into: a held one, or a cell planned for an earlier offer.</summary>
    private sealed class MergeTarget(uint itemId, int count)
    {
        public uint ItemId { get; } = itemId;
        public int Count { get; set; } = count;
        public ulong? HeldUid { get; init; }
        public TradePlannedMove? Created { get; init; }
    }
}
