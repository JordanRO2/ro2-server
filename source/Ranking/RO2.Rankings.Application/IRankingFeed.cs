namespace RO2.Rankings.Application;

/// <summary>Inbound port the gameplay feeders call to add to a ranking counter. Storage errors are logged, not thrown.</summary>
public interface IRankingFeed
{
    /// <summary>Adds <paramref name="amount"/> to the character's counter on <paramref name="rankingType"/>.</summary>
    Task CountAsync(uint characterId, string name, byte rankingType, int amount, CancellationToken ct = default);

    /// <summary>
    /// <paramref name="landedCount"/> of <paramref name="itemId"/> just reached the bag, leaving the stacks of
    /// <paramref name="stackCounts"/>. Counts the new card rows among them; a stack that only grew adds nothing.
    /// </summary>
    Task CountCardLandingAsync(uint characterId, string name, uint itemId, int landedCount, IReadOnlyList<int> stackCounts,
        CancellationToken ct = default);
}
