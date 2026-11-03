using RO2.GameData;
using RO2.Rankings.Domain;

namespace RO2.Rankings.Application;

/// <summary>
/// The count core: adds to a character's counter for the period holding <c>now</c>, and counts the card rows a bag
/// landing opened. A singleton, so the card-id set is built once per loaded store. The Zone host passes the repository
/// and store in (so tests without them stay a no-op), keeps the clock and logs storage errors.
/// </summary>
public sealed class CountRankingUseCase
{
    private (GameDataStore Store, HashSet<uint> Ids)? _cardIds;

    /// <summary>Adds <paramref name="amount"/> to the counter of the period holding <paramref name="now"/>. No FeedOf/amount guard and no try/catch here: the host adapter gates (to keep the clock kick identical) and logs.</summary>
    public async Task AddAsync(IRankingRepository repo, GameDataStore store, uint charId, byte rankingType, int amount,
        DateTime now, CancellationToken ct)
    {
        if (RankingSchedule.ResetRowOf(store, rankingType) is not { } reset)
            return;
        var period = RankingSchedule.PeriodStart(reset, now);
        await repo.AddScoreAsync(rankingType, period, charId, amount, now);
    }

    /// <summary>The new card rows to count when <paramref name="landedCount"/> of <paramref name="itemId"/> reached the bag.</summary>
    public int CardRows(GameDataStore store, uint itemId, int landedCount, IReadOnlyList<int> stackCounts)
        => CardCounting.CardRowsToCount(itemId, CardCounting.NewRowsOf(stackCounts, landedCount), CardIds(store));

    /// <summary>The CardInfo.json Card_IDs, built once per loaded store.</summary>
    private HashSet<uint> CardIds(GameDataStore store)
    {
        var cached = _cardIds;
        if (cached is { } c && ReferenceEquals(c.Store, store))
            return c.Ids;
        var ids = store.CardInfo.Select(r => r.Card_ID).ToHashSet();
        _cardIds = (store, ids);
        return ids;
    }
}
