namespace RO2.Inventory.Domain;

/// <summary>
/// Pure, flow-agnostic bag placement and take planning over neutral bag stacks: where an add lands (restack or new
/// cells) and which stacks a take draws from. It knows nothing of the inventory row type, the game data or any
/// economy flow; the host maps its rows in and resolves the plan back.
/// </summary>
public static class BagStackPlanner
{
    /// <summary>The units one stack of an item holds: its Stack_Max, with 0 and 1 (not stackable) both meaning 1.</summary>
    public static int StackSize(byte stackMax) => stackMax < 1 ? 1 : stackMax;

    /// <summary>
    /// The stack an add of <paramref name="quantity"/> folds into whole: among the stacks of <paramref name="itemId"/>
    /// that are non-empty and still fit it (count + quantity at most the stack size), the one in the lowest cell (the
    /// earlier in <paramref name="owned"/> on a tie). Returns its index in <paramref name="owned"/>, or -1 when none
    /// fits or the item is not stackable.
    /// </summary>
    public static int FindMergeTarget(IReadOnlyList<BagStack> owned, uint itemId, int quantity, byte stackMax)
    {
        int perStack = StackSize(stackMax);
        if (perStack <= 1) return -1;
        return owned
            .Select((s, index) => (Stack: s, Index: index))
            .Where(x => x.Stack.ItemId == itemId && x.Stack.Count > 0 && x.Stack.Count + quantity <= perStack)
            .OrderBy(x => x.Stack.Slot)
            .Select(x => x.Index)
            .DefaultIfEmpty(-1)
            .First();
    }

    /// <summary>
    /// Where an add of <paramref name="quantity"/> lands: onto the lowest-cell stack of the item it fits into
    /// whole (<see cref="FindMergeTarget"/>), else into new stacks of the stack max, each in the next of
    /// <paramref name="freeCells"/>. Null when there are fewer free cells than the new stacks need.
    /// </summary>
    public static PlacementPlan? Plan(IReadOnlyList<BagStack> owned, IReadOnlyList<short> freeCells,
        uint itemId, int quantity, byte stackMax)
    {
        int perStack = StackSize(stackMax);   // ItemInfo: Stack_Max 1 = not stackable
        int target = FindMergeTarget(owned, itemId, quantity, stackMax);
        if (target >= 0) return new PlacementPlan(owned[target].Id, System.Array.Empty<(short, int)>());
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
