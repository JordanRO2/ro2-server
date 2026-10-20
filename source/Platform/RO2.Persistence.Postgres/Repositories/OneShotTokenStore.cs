using Npgsql;
using RO2.Application.Interfaces;

namespace RO2.Persistence.Postgres.Repositories;

/// <summary>
/// PostgreSQL implementation of <see cref="IOneShotTokenStore"/> (SP0-TOKEN). One row per token in the
/// <c>one_shot_tokens</c> table; issue is an INSERT (false on a duplicate token), consume is a single
/// <c>DELETE … WHERE token + purpose + unexpired (+ account when given) RETURNING</c>, so a second consume
/// from any process returns null and a wrong purpose/account never burns the token. Expiry is enforced in the
/// DELETE predicate (<c>expiry &gt; now</c>) exactly as the Mongo store's filter does; the expiry index
/// (created in <see cref="PostgresSchema"/>) is only the garbage collector. The <c>ulong</c> character uid
/// maps to <c>bigint</c> via a bit-preserving cast and the <c>uint</c> account id to <c>bigint</c>; instants
/// are stored UTC, truncated to whole milliseconds to match a BSON-date round-trip.
/// </summary>
public sealed class OneShotTokenStore : IOneShotTokenStore
{
    public const string TableName = "one_shot_tokens";

    private readonly PostgresDbContext _db;
    private readonly TimeProvider _time;

    public OneShotTokenStore(PostgresDbContext db, TimeProvider? time = null)
    {
        _db = db;
        _time = time ?? TimeProvider.System;
    }

    public async Task<bool> IssueAsync(
        string token, string purpose, uint accountId, ulong characterUid, TimeSpan lifetime,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        ArgumentException.ThrowIfNullOrEmpty(purpose);
        if (lifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(lifetime), "token lifetime must be positive");

        var now = Ms(_time.GetUtcNow().UtcDateTime);
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"INSERT INTO {TableName} (token, purpose, account_id, character_uid, issued_utc, expiry) " +
            "VALUES (@t, @p, @a, @c, @i, @e)", conn);
        cmd.Parameters.AddWithValue("t", token);
        cmd.Parameters.AddWithValue("p", purpose);
        cmd.Parameters.AddWithValue("a", (long)accountId);
        cmd.Parameters.AddWithValue("c", unchecked((long)characterUid));
        cmd.Parameters.AddWithValue("i", now);
        cmd.Parameters.AddWithValue("e", Ms(now + lifetime));
        try
        {
            await cmd.ExecuteNonQueryAsync(ct);
            return true;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return false;
        }
    }

    public async Task<string> IssueNewAsync(
        string purpose, uint accountId, ulong characterUid, TimeSpan lifetime, CancellationToken ct = default)
    {
        var token = OneShotTokens.NewToken();
        if (!await IssueAsync(token, purpose, accountId, characterUid, lifetime, ct))
            throw new InvalidOperationException("one-shot token collision"); 
        return token;
    }

    public async Task<OneShotToken?> ConsumeAsync(
        string token, string purpose, uint? accountId = null, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(purpose))
            return null;

        var accountClause = accountId is not null ? " AND account_id = @a" : "";
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"DELETE FROM {TableName} WHERE token = @t AND purpose = @p AND expiry > @now{accountClause} " +
            "RETURNING purpose, account_id, character_uid, issued_utc, expiry", conn);
        cmd.Parameters.AddWithValue("t", token);
        cmd.Parameters.AddWithValue("p", purpose);
        cmd.Parameters.AddWithValue("now", _time.GetUtcNow().UtcDateTime);
        if (accountId is { } acc) cmd.Parameters.AddWithValue("a", (long)acc);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new OneShotToken(
            token,
            reader.GetString(0),
            checked((uint)reader.GetInt64(1)),
            unchecked((ulong)reader.GetInt64(2)),
            DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc),
            DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc));
    }

 /// <summary>No-op: the table and its expiry index are created by <see cref="PostgresSchema"/>.</summary>
    public Task<bool> EnsureIndexesAsync(CancellationToken ct = default) => Task.FromResult(true);

    private static DateTime Ms(DateTime value)
    {
        var utc = value.ToUniversalTime();
        return new DateTime(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
    }
}
