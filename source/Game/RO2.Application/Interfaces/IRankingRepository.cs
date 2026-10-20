using RO2.Domain.Entities;

namespace RO2.Application.Interfaces;

/// <summary>
/// Document persistence for the ranking boards: the per-period counters the game feeds and the published
/// snapshot per type and period. The roll, the clock, the in-process board cache and the rank/tie rules live in
/// the Zone RankingService; this interface is only the stored reads and writes.
/// </summary>
public interface IRankingRepository
{
 /// <summary>Adds <paramref name="amount"/> to the character's counter for the period (upsert), and sets
 /// the counter's last-moved time to <paramref name="reachedUtc"/>.</summary>
    Task AddScoreAsync(byte rankingType, DateTime periodStartUtc, uint charId, long amount, DateTime reachedUtc,
        CancellationToken ct = default);

 /// <summary>The character's counter for the period, 0 when none is stored.</summary>
    Task<long> GetRunningScoreAsync(byte rankingType, DateTime periodStartUtc, uint charId,
        CancellationToken ct = default);

 /// <summary>Every counter stored for the period of the type.</summary>
    Task<IReadOnlyList<RankingScore>> GetPeriodScoresAsync(byte rankingType, DateTime periodStartUtc,
        CancellationToken ct = default);

 /// <summary>The snapshot with the latest period of the type, or null when none was ever published.</summary>
    Task<RankingSnapshotDocument?> GetLatestSnapshotAsync(byte rankingType, CancellationToken ct = default);

 /// <summary>Publishes <paramref name="candidate"/>; first writer wins. Returns the stored board: the
 /// candidate itself when it was inserted, or the one another process inserted first for the same id.</summary>
    Task<RankingSnapshotDocument> PublishSnapshotAsync(RankingSnapshotDocument candidate,
        CancellationToken ct = default);
}
