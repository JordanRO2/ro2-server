using Npgsql;
using NpgsqlTypes;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;

namespace RO2.Persistence.Postgres.Repositories;

/// <summary>
/// PostgreSQL implementation of <see cref="IOnlineAccountRegistry"/>: which accounts hold a Zone connection
/// right now, shared by the Login and Zone processes through the <c>online_accounts</c> table. Mirrors the
/// Mongo provider's behaviour exactly — one row per account (the account id is the primary key, as it was
/// the Mongo <c>_id</c>), owner-guarded heartbeat / transfer / release, and a first-request-wins kick flag.
/// </summary>
/// <remarks>
/// <para>
/// Every conditional Mongo operation becomes one SQL statement whose <c>WHERE</c> carries the same predicate,
/// so the compare-and-set is atomic in the database: <c>Heartbeat</c> is <c>UPDATE … WHERE account_id AND
/// owner RETURNING …</c> (a returned row is the "owner still holds it" case, no row is null);
/// <c>Transfer</c> is <c>UPDATE … WHERE account_id AND owner = from</c> and reports whether exactly one row
/// was affected (so a newer claim by someone else is never overwritten, and a pending kick carries over
/// because <c>kick_requested_at</c> is not touched); <c>RequestKick</c> only updates while
/// <c>kick_requested_at IS NULL</c>, so the first request wins; <c>Release</c> / <c>ReleaseAny</c> are
/// conditional <c>DELETE</c>s (an empty owner list deletes nothing, like Mongo's empty <c>$in</c>).
/// <c>Claim</c> is an upsert that overwrites the whole row and clears <c>kick_requested_at</c>, like the
/// Mongo <c>ReplaceOne</c> with <c>IsUpsert</c>.
/// </para>
/// <para>
/// Liveness (<see cref="OnlineAccountRules.StaleAfter"/>) is judged by the callers from
/// <see cref="OnlineAccountEntry.SeenAt"/>, exactly as with Mongo; the repository does no expiry and the
/// table has no TTL. The <c>uint</c> account id maps to <c>bigint</c> (Postgres has no unsigned integers);
/// all timestamps are <c>timestamptz</c> written from <see cref="DateTime.UtcNow"/> (the application clock,
/// as in the Mongo path) and read back as UTC.
/// </para>
/// </remarks>
public sealed class OnlineAccountRegistry : IOnlineAccountRegistry
{
    public const string TableName = "online_accounts";

    private const string Columns = "account_id, owner, seen_at, kick_requested_at";

    private readonly PostgresDbContext _db;

    public OnlineAccountRegistry(PostgresDbContext db) => _db = db;

    private static OnlineAccountEntry ReadEntry(NpgsqlDataReader reader) => new()
    {
        AccountId = (uint)reader.GetInt64(0),
        Owner = reader.GetString(1),
        SeenAt = reader.GetDateTime(2),
        KickRequestedAt = reader.IsDBNull(3) ? null : reader.GetDateTime(3),
    };

 /// <inheritdoc/>
    public async Task ClaimAsync(uint accountId, string owner, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        
        await using var cmd = new NpgsqlCommand(
            $"INSERT INTO {TableName} (account_id, owner, seen_at, kick_requested_at) VALUES (@a, @o, @s, NULL) " +
            "ON CONFLICT (account_id) DO UPDATE SET owner = EXCLUDED.owner, seen_at = EXCLUDED.seen_at, " +
            "kick_requested_at = NULL",
            conn);
        cmd.Parameters.AddWithValue("a", (long)accountId);
        cmd.Parameters.AddWithValue("o", owner);
        cmd.Parameters.AddWithValue("s", DateTime.UtcNow);
        await cmd.ExecuteNonQueryAsync(ct);
    }

 /// <inheritdoc/>
    public async Task<OnlineAccountEntry?> HeartbeatAsync(uint accountId, string owner, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        
        await using var cmd = new NpgsqlCommand(
            $"UPDATE {TableName} SET seen_at = @s WHERE account_id = @a AND owner = @o RETURNING {Columns}", conn);
        cmd.Parameters.AddWithValue("s", DateTime.UtcNow);
        cmd.Parameters.AddWithValue("a", (long)accountId);
        cmd.Parameters.AddWithValue("o", owner);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadEntry(reader) : null;
    }

 /// <inheritdoc/>
    public async Task<bool> TransferAsync(uint accountId, string fromOwner, string toOwner, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        
        await using var cmd = new NpgsqlCommand(
            $"UPDATE {TableName} SET owner = @to, seen_at = @s WHERE account_id = @a AND owner = @from", conn);
        cmd.Parameters.AddWithValue("to", toOwner);
        cmd.Parameters.AddWithValue("s", DateTime.UtcNow);
        cmd.Parameters.AddWithValue("a", (long)accountId);
        cmd.Parameters.AddWithValue("from", fromOwner);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

 /// <inheritdoc/>
    public async Task ReleaseAsync(uint accountId, string owner, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"DELETE FROM {TableName} WHERE account_id = @a AND owner = @o", conn);
        cmd.Parameters.AddWithValue("a", (long)accountId);
        cmd.Parameters.AddWithValue("o", owner);
        await cmd.ExecuteNonQueryAsync(ct);
    }

 /// <inheritdoc/>
    public async Task ReleaseAnyAsync(uint accountId, IReadOnlyCollection<string> owners, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        
        await using var cmd = new NpgsqlCommand(
            $"DELETE FROM {TableName} WHERE account_id = @a AND owner = ANY(@o)", conn);
        cmd.Parameters.AddWithValue("a", (long)accountId);
        cmd.Parameters.Add(new NpgsqlParameter("o", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = owners.ToArray() });
        await cmd.ExecuteNonQueryAsync(ct);
    }

 /// <inheritdoc/>
    public async Task<OnlineAccountEntry?> GetAsync(uint accountId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"SELECT {Columns} FROM {TableName} WHERE account_id = @a", conn);
        cmd.Parameters.AddWithValue("a", (long)accountId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadEntry(reader) : null;
    }

 /// <inheritdoc/>
    public async Task RequestKickAsync(uint accountId, string owner, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        
        await using var cmd = new NpgsqlCommand(
            $"UPDATE {TableName} SET kick_requested_at = @k " +
            "WHERE account_id = @a AND owner = @o AND kick_requested_at IS NULL",
            conn);
        cmd.Parameters.AddWithValue("k", DateTime.UtcNow);
        cmd.Parameters.AddWithValue("a", (long)accountId);
        cmd.Parameters.AddWithValue("o", owner);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
