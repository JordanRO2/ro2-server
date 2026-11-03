namespace RO2.Rankings.Domain;

/// <summary>The card-collector counting rules.</summary>
public static class CardCounting
{
 /// <summary>
 /// The new rows among the stacks a landing of <paramref name="landedCount"/> items wrote: the stacks that hold no more
 /// than the landed count. A planner that puts the whole count into an existing stack leaves that stack above the landed
 /// count, so only the rows opened by the landing match.
 /// </summary>
    public static int NewRowsOf(IReadOnlyList<int> stackCounts, int landedCount)
        => landedCount <= 0 ? 0 : stackCounts.Count(c => c <= landedCount);

 /// <summary>What <paramref name="newRows"/> new rows of <paramref name="itemId"/> add to type 4: the rows when
 /// the item is a card, else 0.</summary>
    public static int CardRowsToCount(uint itemId, int newRows, IReadOnlySet<uint> cardIds)
        => newRows > 0 && cardIds.Contains(itemId) ? newRows : 0;
}
