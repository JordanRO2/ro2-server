using System.Globalization;
using MongoDB.Driver;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Persistence.Mongo.Persistence;

namespace RO2.Persistence.Mongo.Repositories;

/// <summary>
/// One character's counter on one ranking type for one period (collection <c>ranking_scores</c>).
/// </summary>
internal sealed class RankingScoreDocument
{
 /// <summary><c>"{type}:{periodStart:yyyyMMddHHmm}:{charId}"</c>, so the counter upserts by id.</summary>
    public string Id { get; set; } = string.Empty;
    public uint CharId { get; set; }
    public byte Type { get; set; }

 /// <summary>The period (UTC) the counter belongs to.</summary>
    public DateTime PeriodStartUtc { get; set; }
    public long Score { get; set; }

 /// <summary>When the counter last moved, i.e. when it reached <see cref="Score"/>: the tie-break.</summary>
    public DateTime ReachedUtc { get; set; }
}

/// <summary>
/// MongoDB store for <see cref="IRankingRepository"/> over <c>ranking_scores</c> and <c>ranking_snapshots</c>.
/// Stateless over the process-wide <see cref="MongoDbContext"/>, so it is registered as a singleton (the roll clock
/// holds it beyond any request scope).
/// </summary>
public sealed class RankingRepository : IRankingRepository
{
    private const string ScoresCollection = "ranking_scores";
    private const string SnapshotsCollection = "ranking_snapshots";

    private readonly IMongoCollection<RankingScoreDocument> _scores;
    private readonly IMongoCollection<RankingSnapshotDocument> _snapshots;

    public RankingRepository(MongoDbContext context)
    {
        _scores = context.GetCollection<RankingScoreDocument>(ScoresCollection);
        _snapshots = context.GetCollection<RankingSnapshotDocument>(SnapshotsCollection);
    }

    public Task AddScoreAsync(byte rankingType, DateTime periodStartUtc, uint charId, long amount, DateTime reachedUtc,
        CancellationToken ct = default)
    {
        var id = ScoreId(rankingType, periodStartUtc, charId);
        var update = Builders<RankingScoreDocument>.Update
            .Inc(x => x.Score, amount)
            .Set(x => x.ReachedUtc, reachedUtc)
            .SetOnInsert(x => x.CharId, charId)
            .SetOnInsert(x => x.Type, rankingType)
            .SetOnInsert(x => x.PeriodStartUtc, periodStartUtc);
        return _scores.UpdateOneAsync(x => x.Id == id, update, new UpdateOptions { IsUpsert = true }, ct);
    }

    public async Task<long> GetRunningScoreAsync(byte rankingType, DateTime periodStartUtc, uint charId,
        CancellationToken ct = default)
    {
        var id = ScoreId(rankingType, periodStartUtc, charId);
        var doc = await _scores.Find(x => x.Id == id).FirstOrDefaultAsync(ct);
        return doc?.Score ?? 0;
    }

    public async Task<IReadOnlyList<RankingScore>> GetPeriodScoresAsync(byte rankingType, DateTime periodStartUtc,
        CancellationToken ct = default)
    {
        var docs = await _scores.Find(x => x.Type == rankingType && x.PeriodStartUtc == periodStartUtc).ToListAsync(ct);
        return docs.Select(d => new RankingScore(d.CharId, d.Score, DateTime.SpecifyKind(d.ReachedUtc, DateTimeKind.Utc)))
            .ToList();
    }

    public async Task<RankingSnapshotDocument?> GetLatestSnapshotAsync(byte rankingType, CancellationToken ct = default)
    {
        var latest = await _snapshots.Find(x => x.Type == rankingType)
            .SortByDescending(x => x.PeriodStartUtc).Limit(1).FirstOrDefaultAsync(ct);
        if (latest != null)
            latest.PeriodStartUtc = DateTime.SpecifyKind(latest.PeriodStartUtc, DateTimeKind.Utc);
        return latest;
    }

    public async Task<RankingSnapshotDocument> PublishSnapshotAsync(RankingSnapshotDocument candidate,
        CancellationToken ct = default)
    {
        try
        {
            await _snapshots.InsertOneAsync(candidate, cancellationToken: ct);
            return candidate;
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            
            var winner = await _snapshots.Find(x => x.Id == candidate.Id).FirstOrDefaultAsync(ct) ?? candidate;
            winner.PeriodStartUtc = DateTime.SpecifyKind(winner.PeriodStartUtc, DateTimeKind.Utc);
            return winner;
        }
    }

    internal static string ScoreId(byte rankingType, DateTime periodStartUtc, uint charId) =>
        string.Create(CultureInfo.InvariantCulture, $"{rankingType}:{periodStartUtc:yyyyMMddHHmm}:{charId}");
}
