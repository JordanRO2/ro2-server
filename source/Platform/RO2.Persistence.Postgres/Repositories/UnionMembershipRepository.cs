using Npgsql;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Characters.Domain;

namespace RO2.Persistence.Postgres.Repositories;

/// <summary>
/// PostgreSQL implementation of <see cref="IUnionMembershipRepository"/>: one membership per character in
/// the <c>union_memberships</c> table, keyed by character id. Mirrors the Mongo provider's behaviour exactly.
/// </summary>
/// <remarks>
/// The primary key on <c>character_id</c> is what keeps one membership per character under concurrent
/// 1582s, exactly as the Mongo insert on <c>_id</c> did: <see cref="JoinAsync"/> is a single
/// <c>INSERT … ON CONFLICT DO NOTHING</c> and reports whether a row was inserted (the loser of a race, or a
/// character already in any union, gets false and the existing row is left untouched).
/// <see cref="WithdrawAsync"/> is a single <c>DELETE … WHERE character_id AND union_id</c>, true only when a
/// row was removed. The <c>uint</c> character and union ids map to <c>bigint</c> (Postgres has no unsigned
/// integers); the join time is the <c>bigint</c> unix value as-is, with no timestamp conversion.
/// </remarks>
public sealed class UnionMembershipRepository : IUnionMembershipRepository
{
    public const string TableName = "union_memberships";

    private readonly PostgresDbContext _db;

    public UnionMembershipRepository(PostgresDbContext db) => _db = db;

    public async Task<UnionMembership?> GetAsync(uint characterId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"SELECT union_id, joined_at_unix FROM {TableName} WHERE character_id = @c", conn);
        cmd.Parameters.AddWithValue("c", (long)characterId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new UnionMembership((uint)reader.GetInt64(0), reader.GetInt64(1));
    }

    public async Task<bool> JoinAsync(uint characterId, uint unionId, long joinedAtUnix, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        
        await using var cmd = new NpgsqlCommand(
            $"INSERT INTO {TableName} (character_id, union_id, joined_at_unix) VALUES (@c, @u, @j) " +
            "ON CONFLICT (character_id) DO NOTHING RETURNING character_id", conn);
        cmd.Parameters.AddWithValue("c", (long)characterId);
        cmd.Parameters.AddWithValue("u", (long)unionId);
        cmd.Parameters.AddWithValue("j", joinedAtUnix);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct);
    }

    public async Task<bool> WithdrawAsync(uint characterId, uint unionId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"DELETE FROM {TableName} WHERE character_id = @c AND union_id = @u RETURNING character_id", conn);
        cmd.Parameters.AddWithValue("c", (long)characterId);
        cmd.Parameters.AddWithValue("u", (long)unionId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct);
    }
}
