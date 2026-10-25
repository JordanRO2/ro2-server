namespace RO2.Inventory.Domain;

/// <summary>
/// Pure, flow-agnostic bag placement and take planning over neutral bag stacks: where an add lands (restack or new
/// cells) and which stacks a take draws from. It knows nothing of the inventory row type, the game data or any
/// economy flow; the host maps its rows in and resolves the plan back.
/// </summary>
public static class BagStackPlanner
{
    /// <summary>
    /// Where an add of <paramref name="quantity"/> lands: onto the lowest-cell stack of the item it fits into
    /// whole (existing + quantity ≤ stack max), else into new stacks of the stack max, each in the next of
    /// <paramref name="freeCells"/>. Null when there are fewer free cells than the new stacks need.
    /// </summary>
    public static PlacementPlan? Plan(IReadOnlyList<BagStack> owned, IReadOnlyList<short> freeCells,
        uint itemId, int quantity, byte stackMax)
    {
        int perStack = stackMax < 1 ? 1 : stackMax;   // ItemInfo: Stack_Max 1 = not stackable
        if (perStack > 1)
        {
            var target = owned
                .Where(i => i.ItemId == itemId && i.Count > 0 && i.Count + quantity <= perStack)
                .OrderBy(i => i.Slot)
                .Select(i => (ulong?)i.Id)
                .FirstOrDefault();
            if (target is not null) return new PlacementPlan(target, System.Array.Empty<(short, int)>());
        }
        var stacks = new List<(short Cell, int Count)>();
        for (int remaining = quantity, next = 0; remaining > 0; next++)
        {
            if (next >= freeCells.Count) return null;
            int n = System.Math.Min(perStack, remaining);
            stacks.Add((freeCells[next], n));
            remaining -= n;
        }
        return new PlacementPlan(null, stacks);
    }

    /// <summary>
    /// The stacks of <paramref name="itemId"/> that <paramref name="need"/> units are drawn from, lowest cell first,
    /// and the count each is left with. Excludes <paramref name="excludeId"/> (0 excludes nothing). Empty when
    /// <paramref name="need"/> is non-positive; null when the bag holds fewer than <paramref name="need"/>.
    /// </summary>
    public static IReadOnlyList<StackTake>? TakeFromStacks(IReadOnlyList<BagStack> stacks, uint itemId, int need, ulong excludeId)
    {
        var ordered = stacks
            .Where(i => i.ItemId == itemId && i.Count > 0 && i.Id != excludeId)
            .OrderBy(i => i.Slot)
            .ToList();
        if (need <= 0) return System.Array.Empty<StackTake>();
        if (ordered.Sum(i => (long)i.Count) < need) return null;
        var takes = new List<StackTake>();
        foreach (var s in ordered)
        {
            if (need == 0) break;
            int take = System.Math.Min(need, s.Count);
            takes.Add(new StackTake(s.Id, s.Count - take));
            need -= take;
        }
        return takes;
    }
}
