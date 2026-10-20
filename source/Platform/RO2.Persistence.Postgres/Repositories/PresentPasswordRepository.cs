using System.Security.Cryptography;
using System.Text;
using Npgsql;
using RO2.Application.Interfaces;

namespace RO2.Persistence.Postgres.Repositories;

/// <summary>
/// PostgreSQL implementation of <see cref="IPresentPasswordRepository"/>: one salted hash per account in the
/// <c>present_passwords</c> table (primary key = account id). The password is a 4-digit code; only a salted
/// SHA-256 of <c>"{accountId}:{password}"</c> (upper-case hex) is kept, never the password itself. Mirrors the
/// Mongo provider exactly: same hash, same constant-time comparison, same "no password set means the old
/// value is not checked" rule, same last-write-wins upsert on set.
/// </summary>
/// <remarks>
/// The Mongo path reads the document, checks the old password in-process and then does an unconditional
/// <c>ReplaceOne(upsert)</c>; it is not a compare-and-set, so neither is this one: a <c>SELECT</c> followed by
/// an <c>INSERT … ON CONFLICT DO UPDATE</c>. The <c>uint</c> account id maps to <c>bigint</c> (Postgres has no
/// unsigned integers) and <c>updated_at</c> is a <c>timestamptz</c> written from <see cref="DateTime.UtcNow"/>.
/// </remarks>
public sealed class PresentPasswordRepository : IPresentPasswordRepository
{
    public const string TableName = "present_passwords";

 /// <summary>A gift password is 4 decimal digits (the client's own set-up prompt says so).</summary>
    public const int PasswordLength = 4;

 /// <summary>True when <paramref name="password"/> is a well-formed gift password (exactly four digits).</summary>
    public static bool IsValidPassword(string? password) =>
        password is { Length: PasswordLength } && password.All(c => c is >= '0' and <= '9');

    private readonly PostgresDbContext _db;

    public PresentPasswordRepository(PostgresDbContext db) => _db = db;

    public async Task<bool> HasPasswordAsync(uint accountId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"SELECT 1 FROM {TableName} WHERE account_id = @a", conn);
        cmd.Parameters.AddWithValue("a", (long)accountId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct);
    }

    public async Task<bool> VerifyAsync(uint accountId, string password, CancellationToken ct = default)
    {
        var stored = await GetHashAsync(accountId, ct);
        return stored is not null && FixedEquals(stored, HashOf(accountId, password));
    }

    public async Task<bool> SetAsync(uint accountId, string oldPassword, string newPassword, CancellationToken ct = default)
    {
        if (!IsValidPassword(newPassword)) return false;
        var stored = await GetHashAsync(accountId, ct);
        if (stored is not null && !FixedEquals(stored, HashOf(accountId, oldPassword))) return false;

        await using var conn = await _db.OpenConnectionAsync(ct);
        
        await using var cmd = new NpgsqlCommand(
            $"INSERT INTO {TableName} (account_id, hash, updated_at) VALUES (@a, @h, @u) " +
            "ON CONFLICT (account_id) DO UPDATE SET hash = EXCLUDED.hash, updated_at = EXCLUDED.updated_at",
            conn);
        cmd.Parameters.AddWithValue("a", (long)accountId);
        cmd.Parameters.AddWithValue("h", HashOf(accountId, newPassword));
        cmd.Parameters.AddWithValue("u", DateTime.UtcNow);
        await cmd.ExecuteNonQueryAsync(ct);
        return true;
    }

    private async Task<string?> GetHashAsync(uint accountId, CancellationToken ct)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"SELECT hash FROM {TableName} WHERE account_id = @a", conn);
        cmd.Parameters.AddWithValue("a", (long)accountId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? reader.GetString(0) : null;
    }

    private static string HashOf(uint accountId, string password) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{accountId}:{password}")));

    private static bool FixedEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));
}
