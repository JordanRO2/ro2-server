using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using RO2.Application.Interfaces;

namespace RO2.Persistence.Postgres.Repositories;

/// <summary>
/// PostgreSQL implementation of <see cref="IKharaTraceRepository"/>: a character's Khara tracker pins in the
/// <c>khara_traces</c> table, one row per character keyed by character id. Mirrors the Mongo provider's
/// behaviour exactly: <see cref="GetAsync"/> returns the stored list or an empty list when the character never
/// saved one, and <see cref="SaveAsync"/> is a whole-list upsert (create on first save, full replace afterwards).
/// </summary>
/// <remarks>
/// The list is only ever read and written whole and must come back exactly as the client sent it (order and
/// duplicates preserved, since retail echoes the last 1434 list byte for byte), so it maps to a single
/// <c>jsonb</c> array column on the character's row; a JSON array preserves both order and duplicates. No child
/// table: nothing queries, pages or compare-and-sets inside the list. The <c>uint</c> character id and Khara ids
/// map to <c>bigint</c> / JSON numbers (Postgres has no unsigned integers). The Mongo upsert
/// (<c>ReplaceOne … IsUpsert</c>) is a single <c>INSERT … ON CONFLICT (character_id) DO UPDATE</c>, so two
/// concurrent first saves cannot fail with a duplicate key; the last writer wins, as in Mongo. Saving an empty
/// list stores an empty array (the row exists), and reads back as an empty list.
/// </remarks>
public sealed class KharaTraceRepository : IKharaTraceRepository
{
    public const string TableName = "khara_traces";

    private readonly PostgresDbContext _db;

    public KharaTraceRepository(PostgresDbContext db) => _db = db;

    public async Task<IReadOnlyList<uint>> GetAsync(uint characterId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"SELECT khara_ids FROM {TableName} WHERE character_id = @id", conn);
        cmd.Parameters.AddWithValue("id", (long)characterId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return new List<uint>(); 
        return JsonSerializer.Deserialize<List<uint>>(reader.GetString(0)) ?? new List<uint>();
    }

    public async Task SaveAsync(uint characterId, IReadOnlyList<uint> kharaIds, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"INSERT INTO {TableName} (character_id, khara_ids) VALUES (@id, @ids) " +
            "ON CONFLICT (character_id) DO UPDATE SET khara_ids = EXCLUDED.khara_ids",
            conn);
        cmd.Parameters.AddWithValue("id", (long)characterId);
        cmd.Parameters.AddWithValue("ids", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(kharaIds));
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
