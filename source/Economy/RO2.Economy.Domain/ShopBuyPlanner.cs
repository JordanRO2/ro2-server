using RO2.Inventory.Domain;

namespace RO2.Economy.Domain;

/// <summary>Where a buy lands: either <see cref="RestackId"/> (the whole quantity folds into one existing stack) or
/// <see cref="NewStacks"/> (the (cell, count) of each new stack to create).</summary>
public sealed record ShopBuyPlan(ulong? RestackId, IReadOnlyList<(short Cell, int Count)> NewStacks);

/// <summary>The token items a Buy_Type 1 row costs per unit bought: up to three (template, per-unit need) pairs, as
/// the host reads them from the BuyTokenInfo row (an id of 0 or a need of 0 is ignored).</summary>
public sealed record TokenCost(IReadOnlyList<(uint ItemId, int NeedPerUnit)> Items);

/// <summary>
/// Pure NPC-shop planning over neutral bag stacks: where a buy's items land, whether a buy would pass the item's
/// possession limit, and which stacks a token/stone cost is drawn from. The generic placement and take mechanics
/// live in <see cref="BagStackPlanner"/> (Inventory context); this adds only the shop rules on top.
/// It knows nothing of the inventory row type or the game data; the host maps its rows in and resolves the plan back.
/// </summary>
public static class ShopBuyPlanner
{
    /// <summary>
    /// Where a zeny buy of <paramref name="quantity"/> lands: onto the lowest-cell stack of the item it fits into
    /// whole (existing + quantity ≤ stack max), else into new stacks of the stack max, each in the next of
    /// <paramref name="freeCells"/>. Null when there are fewer free cells than the new stacks need.
    /// </summary>
    public static ShopBuyPlan? Plan(IReadOnlyList<BagStack> owned, IReadOnlyList<short> freeCells,
        uint itemId, int quantity, byte stackMax)
    {
        var plan = BagStackPlanner.Plan(owned, freeCells, itemId, quantity, stackMax);
        return plan is null ? null : new ShopBuyPlan(plan.RestackId, plan.NewStacks);
    }

    /// <summary>
    /// True when buying <paramref name="quantity"/> more of <paramref name="itemId"/> would put the bag total above
    /// <paramref name="limit"/>. A non-positive limit is never exceeded. <paramref name="held"/> is the bag total.
    /// </summary>
    public static bool ExceedsPossessionLimit(IReadOnlyList<BagStack> owned, uint itemId, long quantity, int limit, out long held)
    {
        held = 0;
        if (limit <= 0) return false;
        held = owned.Where(i => i.ItemId == itemId).Sum(i => (long)System.Math.Max(0, i.Count));
        return held + quantity > limit;
    }

    /// <summary>
    /// The token stacks a Buy_Type 1 buy of <paramref name="quantity"/> takes and the count each is left with:
    /// per-unit need × quantity of each token template (a template named twice adds up), drawn lowest cell first.
    /// Null when the bag holds fewer than any one aggregated need.
    /// </summary>
    public static IReadOnlyList<StackTake>? PlanTokenTake(IReadOnlyList<BagStack> owned, TokenCost cost, int quantity)
    {
        var needs = new List<(uint ItemId, int Need)>(3);
        foreach (var (id, need) in cost.Items)
        {
            if (id == 0 || need <= 0) continue;
            int i = needs.FindIndex(n => n.ItemId == id);
            if (i >= 0) needs[i] = (id, needs[i].Need + need * quantity);
            else needs.Add((id, need * quantity));
        }
        var takes = new List<StackTake>();
        foreach (var (id, need) in needs)
        {
            var part = BagStackPlanner.TakeFromStacks(owned, id, need, 0);
            if (part is null) return null;
            takes.AddRange(part);
        }
        return takes;
    }
}
