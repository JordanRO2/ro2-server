using Npgsql;
using RO2.Application.Interfaces;

namespace RO2.Persistence.Postgres.Repositories;

/// <summary>
/// PostgreSQL implementation of <see cref="IZoneAuthTokenRepository"/>: the shared Login->Zone handoff
/// token in the <c>zone_auth_tokens</c> table. Mirrors the Mongo provider's behaviour exactly —
/// Login issues (upsert), Zone consumes once (atomic delete-and-return, expiry checked after).
/// </summary>
/// <remarks>
/// The one-time consume is a single <c>DELETE … RETURNING</c>: the row is removed whether or not it is
/// still valid (so an expired token is also purged, as the Mongo <c>FindOneAndDelete</c> did), and the
/// expiry is judged in-process against <see cref="DateTime.UtcNow"/> rather than the database clock, to
/// match the Mongo path byte-for-byte. Issuing also prunes expired rows (the stand-in for Mongo's TTL index) and
/// supersedes the account's older tokens (S-H8). The <c>uint</c> account id maps to <c>bigint</c> (Postgres has no
/// unsigned integers); the token is a native <c>uuid</c>.
/// </remarks>
public sealed class ZoneAuthTokenRepository : IZoneAuthTokenRepository
{
    public const string TableName = "zone_auth_tokens";

    private readonly PostgresDbContext _db;

    public ZoneAuthTokenRepository(PostgresDbContext db) => _db = db;

    public async Task IssueAsync(Guid token, uint accountId, TimeSpan ttl)
    {
        await using var conn = await _db.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        var now = DateTime.UtcNow;
        var expiresAt = now.Add(ttl);

        // Upsert so a regenerated GUID for the same handoff simply overwrites.
        await using (var cmd = new NpgsqlCommand(
            $"INSERT INTO {TableName} (token, account_id, expires_at) VALUES (@t, @a, @e) " +
            "ON CONFLICT (token) DO UPDATE SET account_id = EXCLUDED.account_id, expires_at = EXCLUDED.expires_at",
            conn, tx))
        {
            cmd.Parameters.AddWithValue("t", token);
            cmd.Parameters.AddWithValue("a", (long)accountId);
            cmd.Parameters.AddWithValue("e", expiresAt);
            await cmd.ExecuteNonQueryAsync();
        }

        // S-H8: Postgres has no TTL index, so every issue prunes the expired rows, and the new token supersedes
        // the account's older ones (by expiry), leaving one live token per account. Deleting only the older ones
        // keeps two racing issues from deleting each other's token, so the later one always survives.
        await using (var prune = new NpgsqlCommand(
            $"DELETE FROM {TableName} WHERE expires_at < @now " +
            "OR (account_id = @a AND token <> @t AND expires_at < @e)",
            conn, tx))
        {
            prune.Parameters.AddWithValue("now", now);
            prune.Parameters.AddWithValue("a", (long)accountId);
            prune.Parameters.AddWithValue("t", token);
            prune.Parameters.AddWithValue("e", expiresAt);
            await prune.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
    }

    public async Task<uint?> ConsumeAsync(Guid token)
    {
        await using var conn = await _db.OpenConnectionAsync();
        // One-time use: atomically delete the row and return what it held.
        await using var cmd = new NpgsqlCommand(
            $"DELETE FROM {TableName} WHERE token = @t RETURNING account_id, expires_at", conn);
        cmd.Parameters.AddWithValue("t", token);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;              // no such token
        var accountId = (uint)reader.GetInt64(0);
        var expiresAt = reader.GetDateTime(1);
        if (expiresAt < DateTime.UtcNow) return null;            // presented too late (row already purged)
        return accountId;
    }
}
