using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;

namespace RO2.Persistence.Postgres.Repositories;

/// <summary>
/// PostgreSQL implementation of <see cref="IRankingRepository"/> over <c>ranking_scores</c> and
/// <c>ranking_snapshots</c>. Mirrors the Mongo provider's behaviour exactly: the per-period counters upsert
/// (add to the score, move the last-reached time), and a snapshot is published once per id, first writer wins.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scores.</b> One row per (ranking type, period start, character) — the composite primary key replaces the
/// Mongo string id <c>"{type}:{periodStart:yyyyMMddHHmm}:{charId}"</c>. <see cref="AddScoreAsync"/> is one
/// atomic <c>INSERT … ON CONFLICT DO UPDATE</c> that adds to the score and sets the reached time, the SQL
/// equivalent of the Mongo <c>$inc</c> + <c>$set</c> + <c>$setOnInsert</c> upsert (a missing counter starts
/// from the amount, as <c>$inc</c> on an absent field does).
/// </para>
/// <para>
/// <b>Snapshots.</b> One row per published board keyed by the caller-supplied text id (as the Mongo <c>_id</c>
/// was). The board's rows are never queried or compare-and-set individually — the whole board is read and
/// written as a unit — so they live in one ordered <c>jsonb</c> array rather than a child table.
/// <see cref="PublishSnapshotAsync"/> is a single <c>INSERT … ON CONFLICT (id) DO NOTHING RETURNING id</c>: a
/// returned row means the candidate was inserted (it is returned as is); no row means another writer owns the
/// id, whose stored board is then read back and returned (the Mongo duplicate-key path), falling back to the
/// candidate if that row has vanished.
/// </para>
/// <para>
/// <c>uint</c>/<c>byte</c> values map to <c>bigint</c>/<c>smallint</c> (Postgres has no unsigned integers).
/// Every <see cref="DateTime"/> is normalised to UTC and truncated to whole milliseconds on write, which is
/// what a BSON date round-trip did (so the tie-break instants and the exact period match behave identically),
/// and reads back with Kind=Utc.
/// </para>
/// </remarks>
public sealed class RankingRepository : IRankingRepository
{
    public const string ScoresTable = "ranking_scores";
    public const string SnapshotsTable = "ranking_snapshots";

    private readonly PostgresDbContext _db;

    public RankingRepository(PostgresDbContext db) => _db = db;

    public async Task AddScoreAsync(byte rankingType, DateTime periodStartUtc, uint charId, long amount,
        DateTime reachedUtc, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"INSERT INTO {ScoresTable} (ranking_type, period_start_utc, char_id, score, reached_utc) " +
            "VALUES (@t, @p, @c, @amt, @r) " +
            "ON CONFLICT (ranking_type, period_start_utc, char_id) DO UPDATE SET " +
            $"score = {ScoresTable}.score + EXCLUDED.score, reached_utc = EXCLUDED.reached_utc",
            conn);
        cmd.Parameters.AddWithValue("t", (short)rankingType);
        cmd.Parameters.AddWithValue("p", ToUtcMilliseconds(periodStartUtc));
        cmd.Parameters.AddWithValue("c", (long)charId);
        cmd.Parameters.AddWithValue("amt", amount);
        cmd.Parameters.AddWithValue("r", ToUtcMilliseconds(reachedUtc));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<long> GetRunningScoreAsync(byte rankingType, DateTime periodStartUtc, uint charId,
        CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"SELECT score FROM {ScoresTable} WHERE ranking_type = @t AND period_start_utc = @p AND char_id = @c",
            conn);
        cmd.Parameters.AddWithValue("t", (short)rankingType);
        cmd.Parameters.AddWithValue("p", ToUtcMilliseconds(periodStartUtc));
        cmd.Parameters.AddWithValue("c", (long)charId);
        var scalar = await cmd.ExecuteScalarAsync(ct);
        return scalar is long score ? score : 0;               
    }

    public async Task<IReadOnlyList<RankingScore>> GetPeriodScoresAsync(byte rankingType, DateTime periodStartUtc,
        CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"SELECT char_id, score, reached_utc FROM {ScoresTable} WHERE ranking_type = @t AND period_start_utc = @p",
            conn);
        cmd.Parameters.AddWithValue("t", (short)rankingType);
        cmd.Parameters.AddWithValue("p", ToUtcMilliseconds(periodStartUtc));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var result = new List<RankingScore>();
        while (await reader.ReadAsync(ct))
        {
            result.Add(new RankingScore(
                (uint)reader.GetInt64(0),
                reader.GetInt64(1),
                DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc)));
        }
        return result;
    }

    public async Task<RankingSnapshotDocument?> GetLatestSnapshotAsync(byte rankingType,
        CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"SELECT id, ranking_type, period_start_utc, rolled_utc, board_rows FROM {SnapshotsTable} " +
            "WHERE ranking_type = @t ORDER BY period_start_utc DESC LIMIT 1",
            conn);
        cmd.Parameters.AddWithValue("t", (short)rankingType);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadSnapshot(reader) : null;
    }

    public async Task<RankingSnapshotDocument> PublishSnapshotAsync(RankingSnapshotDocument candidate,
        CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(candidate.Rows ?? new List<RankingSnapshotRow>());

        await using var conn = await _db.OpenConnectionAsync(ct);
        await using (var insert = new NpgsqlCommand(
            $"INSERT INTO {SnapshotsTable} (id, ranking_type, period_start_utc, rolled_utc, board_rows) " +
            "VALUES (@id, @t, @p, @r, @rows) ON CONFLICT (id) DO NOTHING RETURNING id",
            conn))
        {
            insert.Parameters.AddWithValue("id", candidate.Id);
            insert.Parameters.AddWithValue("t", (short)candidate.Type);
            insert.Parameters.AddWithValue("p", ToUtcMilliseconds(candidate.PeriodStartUtc));
            insert.Parameters.AddWithValue("r", ToUtcMilliseconds(candidate.RolledUtc));
            insert.Parameters.Add(new NpgsqlParameter("rows", NpgsqlDbType.Jsonb) { Value = json });
            
            if (await insert.ExecuteScalarAsync(ct) is not null) return candidate;
        }

        
        await using var select = new NpgsqlCommand(
            $"SELECT id, ranking_type, period_start_utc, rolled_utc, board_rows FROM {SnapshotsTable} WHERE id = @id",
            conn);
        select.Parameters.AddWithValue("id", candidate.Id);
        await using var reader = await select.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadSnapshot(reader) : candidate;
    }

    private static RankingSnapshotDocument ReadSnapshot(NpgsqlDataReader reader) => new()
    {
        Id = reader.GetString(0),
        Type = (byte)reader.GetInt16(1),
        PeriodStartUtc = DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc),
        RolledUtc = DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc),
        Rows = JsonSerializer.Deserialize<List<RankingSnapshotRow>>(reader.GetString(4)) ?? new List<RankingSnapshotRow>(),
    };

 /// <summary>UTC with Kind=Utc (Npgsql rejects anything else for timestamptz), truncated to whole milliseconds.</summary>
    private static DateTime ToUtcMilliseconds(DateTime value)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
        return new DateTime(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
    }
}
