using System.Text.Json;
using Npgsql;
using RO2.Persistence.Postgres;
using RO2.Socials.Domain;

namespace RO2.Socials.Infrastructure.Postgres;

/// <summary>
/// PostgreSQL implementation of <see cref="IFriendListRepository"/>: one row per owner character in
/// <c>chat_friend_lists</c>, the friend ids held in insertion order as a <c>jsonb</c> array (the Mongo
/// <c>FriendIds</c> array). Mirrors the Mongo provider: same outcomes, same order, same 200 cap.
/// </summary>
/// <remarks>
/// A jsonb array keeps order and lets the cap and the dedup be tested on the same row version that is
/// appended to, which a child table would need an explicit lock for. The add is ONE
/// <c>INSERT … ON CONFLICT DO UPDATE … WHERE</c>: Postgres locks the conflicting row and re-evaluates the WHERE
/// against its latest committed version, so two concurrent adds that both saw 199 serialise and only the first
/// appends; and two concurrent first-inserts resolve through the same conflict path (no duplicate-key retry
/// needed). uint ids map to <c>bigint</c> / JSON numbers.
/// </remarks>
public sealed class PostgresFriendListRepository : IFriendListRepository
{
    public const string TableName = "chat_friend_lists";

    private readonly PostgresDbContext _db;

    public PostgresFriendListRepository(PostgresDbContext db) => _db = db;

    public async Task<IReadOnlyList<uint>> LoadAsync(uint ownerId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand($"SELECT friend_ids FROM {TableName} WHERE owner_id = @o", conn);
        cmd.Parameters.AddWithValue("o", (long)ownerId);
        var json = await cmd.ExecuteScalarAsync(ct) as string;
        return json is null ? Array.Empty<uint>() : JsonSerializer.Deserialize<List<uint>>(json)!;
    }

    public async Task<AddOutcome> AddAsync(uint ownerId, uint friendId)
    {
        await using var conn = await _db.OpenConnectionAsync();
        // Appends only while the list has room and does not hold the id; a row is returned only when it appended.
        await using var add = new NpgsqlCommand(
            $"INSERT INTO {TableName} AS t (owner_id, friend_ids) VALUES (@o, jsonb_build_array(@f::bigint)) " +
            "ON CONFLICT (owner_id) DO UPDATE SET friend_ids = t.friend_ids || jsonb_build_array(@f::bigint) " +
            $"WHERE jsonb_array_length(t.friend_ids) < {SocialRules.MaxFriends} " +
            "AND NOT t.friend_ids @> jsonb_build_array(@f::bigint) RETURNING 1", conn);
        add.Parameters.AddWithValue("o", (long)ownerId);
        add.Parameters.AddWithValue("f", (long)friendId);
        if (await add.ExecuteScalarAsync() is not null) return AddOutcome.Added;

        // The row exists and was left untouched: already listed, or full without it.
        await using var probe = new NpgsqlCommand(
            $"SELECT friend_ids @> jsonb_build_array(@f::bigint) FROM {TableName} WHERE owner_id = @o", conn);
        probe.Parameters.AddWithValue("o", (long)ownerId);
        probe.Parameters.AddWithValue("f", (long)friendId);
        return await probe.ExecuteScalarAsync() is true ? AddOutcome.AlreadyFriend : AddOutcome.ListFull;
    }

    public async Task RemoveAsync(uint ownerId, uint friendId)
    {
        await using var conn = await _db.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            $"UPDATE {TableName} SET friend_ids = COALESCE((SELECT jsonb_agg(e ORDER BY ord) " +
            "FROM jsonb_array_elements(friend_ids) WITH ORDINALITY AS x(e, ord) WHERE e <> to_jsonb(@f::bigint)), '[]'::jsonb) " +
            "WHERE owner_id = @o AND friend_ids @> jsonb_build_array(@f::bigint)", conn);
        cmd.Parameters.AddWithValue("o", (long)ownerId);
        cmd.Parameters.AddWithValue("f", (long)friendId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<bool> ListsFriendAsync(uint listOwnerId, uint friendId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"SELECT EXISTS (SELECT 1 FROM {TableName} WHERE owner_id = @o AND friend_ids @> jsonb_build_array(@f::bigint))", conn);
        cmd.Parameters.AddWithValue("o", (long)listOwnerId);
        cmd.Parameters.AddWithValue("f", (long)friendId);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<IReadOnlyList<uint>> OwnersListingAsync(uint friendId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"SELECT owner_id FROM {TableName} WHERE friend_ids @> jsonb_build_array(@f::bigint)", conn);
        cmd.Parameters.AddWithValue("f", (long)friendId);
        var owners = new List<uint>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) owners.Add((uint)reader.GetInt64(0));
        return owners;
    }
}
