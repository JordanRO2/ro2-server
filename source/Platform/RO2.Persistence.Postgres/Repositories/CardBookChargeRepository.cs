using Npgsql;
using RO2.Application.Interfaces;

namespace RO2.Persistence.Postgres.Repositories;

/// <summary>
/// PostgreSQL implementation of <see cref="ICardBookChargeRepository"/>: one row per character in the
/// <c>card_book_charges</c> table (character id -> UTC charge expiry). Mirrors the Mongo provider exactly.
/// The row is kept once written, lapsed or not, so "never charged" (no row, null) and "lapsed" (a past
/// instant) stay different answers.
/// </summary>
/// <remarks>
/// The <c>uint</c> character id maps to <c>bigint</c> (Postgres has no unsigned integers) and is the primary
/// key, the way it was the Mongo <c>_id</c>. The expiry is a <c>timestamptz</c>, always written and returned
/// as <see cref="DateTimeKind.Utc"/>. The expiry is extended by exactly <c>days</c> days in the server's
/// LOCAL terms (the wire carries the retail server's local clock): <c>nowUtc</c> and the stored expiry are
/// converted to local time, compared and advanced there, and only the result is converted back to UTC.
/// <see cref="ExtendChargeAsync"/> is a read followed by an upsert, exactly as the Mongo path is a
/// read followed by an upserting <c>ReplaceOne</c>: two concurrent extends of one character can lose one
/// (Phase 1 keeps that), so there is no compare-and-set predicate to map.
/// </remarks>
public sealed class CardBookChargeRepository : ICardBookChargeRepository
{
    public const string TableName = "card_book_charges";

    private readonly PostgresDbContext _db;

    public CardBookChargeRepository(PostgresDbContext db) => _db = db;

    public async Task<DateTime?> GetChargedUntilUtcAsync(uint characterId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"SELECT charged_until_utc FROM {TableName} WHERE character_id = @c", conn);
        cmd.Parameters.AddWithValue("c", (long)characterId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;            
        return DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc);
    }

    public async Task<DateTime> ExtendChargeAsync(uint characterId, int days, DateTime nowUtc, CancellationToken ct = default)
    {
        DateTime now = nowUtc.ToLocalTime();
        DateTime? currentUtc = await GetChargedUntilUtcAsync(characterId, ct);
        DateTime? current = currentUtc?.ToLocalTime();
        DateTime from = current is { } stillCharged && stillCharged > now ? stillCharged : now;
        DateTime untilUtc = from.AddDays(days).ToUniversalTime();

        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"INSERT INTO {TableName} (character_id, charged_until_utc, updated_at) VALUES (@c, @u, @t) " +
            "ON CONFLICT (character_id) DO UPDATE SET " +
            "charged_until_utc = EXCLUDED.charged_until_utc, updated_at = EXCLUDED.updated_at",
            conn);
        cmd.Parameters.AddWithValue("c", (long)characterId);
        cmd.Parameters.AddWithValue("u", DateTime.SpecifyKind(untilUtc, DateTimeKind.Utc));
        cmd.Parameters.AddWithValue("t", DateTime.UtcNow);
        await cmd.ExecuteNonQueryAsync(ct);
        return untilUtc;
    }
}
