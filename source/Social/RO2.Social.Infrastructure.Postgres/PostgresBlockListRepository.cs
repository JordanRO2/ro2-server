using System.Text.Json;
using Npgsql;
using RO2.Persistence.Postgres;
using RO2.Socials.Domain;

namespace RO2.Socials.Infrastructure.Postgres;

/// <summary>
/// PostgreSQL implementation of <see cref="IBlockListRepository"/>: one row per owner character in
/// <c>chat_block_lists</c>, the entries held in insertion order as a <c>jsonb</c> array of
/// <c>{"uid": number, "name": text}</c> objects (the Mongo <c>Entries</c> array). Mirrors the Mongo provider.
/// </summary>
/// <remarks>
/// Same shape and atomicity as <see cref="PostgresFriendListRepository"/>: the add is one
/// <c>INSERT … ON CONFLICT DO UPDATE … WHERE</c> guarded by not-present-by-uid and below
/// <see cref="SocialRules.MaxBlocked"/>. Presence is matched on the bare <c>uid</c> only (a stored name never
/// takes part), exactly as the Mongo guard does.
/// </remarks>
public sealed class PostgresBlockListRepository : IBlockListRepository
{
    public const string TableName = "chat_block_lists";

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly PostgresDbContext _db;

    public PostgresBlockListRepository(PostgresDbContext db) => _db = db;

    private sealed record Entry(ulong Uid, string? Name);

    public async Task<IReadOnlyList<BlockListEntry>> GetStoredEntriesAsync(uint ownerId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand($"SELECT entries FROM {TableName} WHERE owner_id = @o", conn);
        cmd.Parameters.AddWithValue("o", (long)ownerId);
        var json = await cmd.ExecuteScalarAsync(ct) as string;
        if (json is null) return Array.Empty<BlockListEntry>();
        return JsonSerializer.Deserialize<List<Entry>>(json, Json)!
            .Select(e => new BlockListEntry { Uid = e.Uid, Name = e.Name ?? string.Empty }).ToList();
    }

    public async Task<BlockAddOutcome> AddAsync(uint ownerId, uint characterId, string name, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var add = new NpgsqlCommand(
            $"INSERT INTO {TableName} AS t (owner_id, entries) " +
            "VALUES (@o, jsonb_build_array(jsonb_build_object('uid', @u::bigint, 'name', @n::text))) " +
            "ON CONFLICT (owner_id) DO UPDATE SET entries = t.entries || EXCLUDED.entries " +
            $"WHERE jsonb_array_length(t.entries) < {SocialRules.MaxBlocked} " +
            "AND NOT t.entries @> jsonb_build_array(jsonb_build_object('uid', @u::bigint)) RETURNING 1", conn);
        add.Parameters.AddWithValue("o", (long)ownerId);
        add.Parameters.AddWithValue("u", (long)characterId);
        add.Parameters.AddWithValue("n", name);
        if (await add.ExecuteScalarAsync(ct) is not null) return BlockAddOutcome.Added;

        return await HoldsAsync(conn, ownerId, characterId, ct) ? BlockAddOutcome.AlreadyRegistered : BlockAddOutcome.ListFull;
    }

    public async Task<bool> BlocksAsync(uint ownerId, uint characterId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        return await HoldsAsync(conn, ownerId, characterId, ct);
    }

    public async Task RemoveAsync(uint ownerId, ulong uid, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"UPDATE {TableName} SET entries = COALESCE((SELECT jsonb_agg(e ORDER BY ord) " +
            "FROM jsonb_array_elements(entries) WITH ORDINALITY AS x(e, ord) WHERE e -> 'uid' <> to_jsonb(@u::bigint)), '[]'::jsonb) " +
            "WHERE owner_id = @o AND entries @> jsonb_build_array(jsonb_build_object('uid', @u::bigint))", conn);
        cmd.Parameters.AddWithValue("o", (long)ownerId);
        cmd.Parameters.AddWithValue("u", (long)CharacterGid.CharacterIdOf(uid));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlySet<uint>> OwnersBlockingAsync(
        IReadOnlyCollection<uint> ownerIds, uint characterId, CancellationToken ct = default)
    {
        var blockers = new HashSet<uint>();
        if (ownerIds.Count == 0) return blockers;

        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"SELECT owner_id FROM {TableName} WHERE owner_id = ANY(@ids) " +
            "AND entries @> jsonb_build_array(jsonb_build_object('uid', @u::bigint))", conn);
        cmd.Parameters.AddWithValue("ids", ownerIds.Select(i => (long)i).ToArray());
        cmd.Parameters.AddWithValue("u", (long)characterId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) blockers.Add((uint)reader.GetInt64(0));
        return blockers;
    }

    private static async Task<bool> HoldsAsync(NpgsqlConnection conn, uint ownerId, uint characterId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"SELECT EXISTS (SELECT 1 FROM {TableName} WHERE owner_id = @o " +
            "AND entries @> jsonb_build_array(jsonb_build_object('uid', @u::bigint)))", conn);
        cmd.Parameters.AddWithValue("o", (long)ownerId);
        cmd.Parameters.AddWithValue("u", (long)characterId);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }
}
