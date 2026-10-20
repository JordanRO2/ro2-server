using Npgsql;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;

namespace RO2.Persistence.Postgres.Repositories;

/// <summary>
/// PostgreSQL implementation of <see cref="IPlayTimeRewardRepository"/> over the
/// <c>account_playtime_reward</c> table (one row per account, keyed by account id). Mirrors the Mongo
/// provider: raw document I/O only (read one, upsert one, upsert many); the Zone service owns the cache,
/// the credit/save clock and the chain rules.
/// </summary>
/// <remarks>
/// The document is flat, so it maps to plain columns rather than jsonb. The <c>uint</c> account id and slot id
/// map to <c>bigint</c> (Postgres has no unsigned integers) and are read back with a <c>(uint)</c> cast;
/// <c>AccumulatedSeconds</c> is <c>double precision</c>; the two timestamps are <c>timestamptz</c> and are
/// always passed with <see cref="DateTimeKind.Utc"/>. The Mongo <c>ReplaceOne(IsUpsert)</c> is a single
/// <c>INSERT … ON CONFLICT (account_id) DO UPDATE</c> that overwrites every column. The unordered bulk upsert
/// runs in one transaction on one connection, applying the documents in order, so a duplicated id resolves to
/// the last document and a failure leaves nothing half-written.
/// </remarks>
public sealed class PlayTimeRewardRepository : IPlayTimeRewardRepository
{
    public const string TableName = "account_playtime_reward";

    private const string UpsertSql =
        $"INSERT INTO {TableName} (account_id, slot_id, accumulated_seconds, period_start_utc, updated_utc) " +
        "VALUES (@id, @slot, @acc, @period, @updated) " +
        "ON CONFLICT (account_id) DO UPDATE SET slot_id = EXCLUDED.slot_id, " +
        "accumulated_seconds = EXCLUDED.accumulated_seconds, period_start_utc = EXCLUDED.period_start_utc, " +
        "updated_utc = EXCLUDED.updated_utc";

    private readonly PostgresDbContext _db;

    public PlayTimeRewardRepository(PostgresDbContext db) => _db = db;

    public async Task<PlayTimeRewardDocument?> GetDocumentAsync(uint accountId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"SELECT slot_id, accumulated_seconds, period_start_utc, updated_utc FROM {TableName} " +
            "WHERE account_id = @id", conn);
        cmd.Parameters.AddWithValue("id", (long)accountId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new PlayTimeRewardDocument
        {
            Id = accountId,
            SlotId = (uint)reader.GetInt64(0),
            AccumulatedSeconds = reader.GetDouble(1),
            PeriodStartUtc = reader.GetDateTime(2),
            UpdatedUtc = reader.GetDateTime(3),
        };
    }

    public async Task SaveDocumentAsync(PlayTimeRewardDocument doc, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = BuildUpsert(conn, null, doc);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SaveManyDocumentsAsync(IReadOnlyCollection<PlayTimeRewardDocument> docs, CancellationToken ct = default)
    {
        if (docs.Count == 0)
            return;
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        foreach (var doc in docs)
        {
            await using var cmd = BuildUpsert(conn, tx, doc);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    private static NpgsqlCommand BuildUpsert(NpgsqlConnection conn, NpgsqlTransaction? tx, PlayTimeRewardDocument doc)
    {
        var cmd = new NpgsqlCommand(UpsertSql, conn, tx);
        cmd.Parameters.AddWithValue("id", (long)doc.Id);
        cmd.Parameters.AddWithValue("slot", (long)doc.SlotId);
        cmd.Parameters.AddWithValue("acc", doc.AccumulatedSeconds);
        cmd.Parameters.AddWithValue("period", AsUtc(doc.PeriodStartUtc));
        cmd.Parameters.AddWithValue("updated", AsUtc(doc.UpdatedUtc));
        return cmd;
    }

    
    
    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
