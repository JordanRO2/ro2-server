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
    /// The counts of the new stacks an add of <paramref name="quantity"/> opens: full stacks of the stack max
    /// first, then the remainder. Empty when <paramref name="quantity"/> is not positive.
    /// </summary>
    public static IReadOnlyList<int> ChunkCounts(int quantity, byte stackMax)
    {
        int perStack = StackSize(stackMax);
        var counts = new List<int>();
        for (int remaining = quantity; remaining > 0;)
        {
            int n = System.Math.Min(perStack, remaining);
            counts.Add(n);
            remaining -= n;
        }
        return counts;
    }

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
        int target = FindMergeTarget(owned, itemId, quantity, stackMax);
        if (target >= 0) return new PlacementPlan(owned[target].Id, System.Array.Empty<(short, int)>());
        var counts = ChunkCounts(quantity, stackMax);
        if (counts.Count > freeCells.Count) return null;
        var stacks = new List<(short Cell, int Count)>(counts.Count);
        for (int i = 0; i < counts.Count; i++)
            stacks.Add((freeCells[i], counts[i]));
        return new PlacementPlan(null, stacks);
    }

    /// <summary>
    /// Plans a SEQUENCE of adds into the bag in one pass, as the kill auto-loot and dungeon-finder reward do: each
    /// entry folds its whole quantity into the lowest-cell stack of its item that still fits (an existing stack OR one
    /// opened by an earlier entry in this sequence), else opens new stacks of the stack max in the remaining free
    /// cells, advancing a shared cell cursor; an entry that cannot fit is refused and consumes no cell (so later
    /// entries may still place). Returns one element per entry, in order, with a null element for a skipped entry
    /// (no item id, or non-positive quantity). The caller maps each cell back to its row and keeps its own
    /// all-or-nothing vs per-entry refusal policy.
    /// </summary>
    /// <param name="owned">The bag stacks already held (the caller filters to real bag rows: the main bag and equipped bags).</param>
    /// <param name="freeCells">The free cells, lowest first, that new stacks are opened into.</param>
    /// <param name="entries">The adds, in order: the item, the quantity, and the item's stack max.</param>
    public static IReadOnlyList<SequencedPlacement?> PlanSequence(
        IReadOnlyList<BagStack> owned, IReadOnlyList<short> freeCells,
        IReadOnlyList<(uint ItemId, int Quantity, byte StackMax)> entries)
    {
        var result = new SequencedPlacement?[entries.Count];
        int nextCell = 0;
        // Working stacks: the existing rows, then each stack this sequence opens (so later entries can merge into them).
        var stacks = new List<(uint ItemId, short Slot, int Count)>();
        foreach (var s in owned)
            if (s.Count > 0) stacks.Add((s.ItemId, s.Slot, s.Count));

        for (int k = 0; k < entries.Count; k++)
        {
            var (itemId, quantity, stackMax) = entries[k];
            if (itemId == 0 || quantity <= 0) { result[k] = null; continue; }
            int perStack = StackSize(stackMax);
            if (perStack > 1)
            {
                int target = -1;
                for (int s = 0; s < stacks.Count; s++)
                {
                    var c = stacks[s];
                    if (c.ItemId != itemId || c.Count + quantity > perStack) continue;
                    if (target < 0 || c.Slot < stacks[target].Slot) target = s;
                }
                if (target >= 0)
                {
                    int merged = stacks[target].Count + quantity;
                    stacks[target] = (stacks[target].ItemId, stacks[target].Slot, merged);
                    result[k] = new SequencedPlacement(false, stacks[target].Slot, merged, System.Array.Empty<(short, int)>());
                    continue;
                }
            }

            var counts = ChunkCounts(quantity, stackMax);
            if (freeCells.Count - nextCell < counts.Count)
            {
                result[k] = new SequencedPlacement(true, null, 0, System.Array.Empty<(short, int)>());
                continue;
            }
            var opened = new List<(short Cell, int Count)>(counts.Count);
            foreach (int n in counts)
            {
                short cell = freeCells[nextCell++];
                opened.Add((cell, n));
                stacks.Add((itemId, cell, n));
            }
            result[k] = new SequencedPlacement(false, null, 0, opened);
        }
        return result;
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
