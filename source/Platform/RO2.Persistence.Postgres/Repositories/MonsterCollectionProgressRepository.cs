using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;

namespace RO2.Persistence.Postgres.Repositories;

/// <summary>
/// PostgreSQL implementation of <see cref="IMonsterCollectionProgressRepository"/> over the
/// <c>collection_monster_progress</c> table: one row per character, keyed by character id, holding the whole
/// progress document. Mirrors the Mongo provider exactly - raw document I/O (errors propagate), a whole-document
/// upsert on save, and the same known lost-update window between two concurrent read-modify-write feeds.
/// </summary>
/// <remarks>
/// The entry list is never queried, paged or compare-and-set on its own (the interface only gets and replaces the
/// whole document), so it is one <c>entries</c> jsonb array serialized with System.Text.Json, in list order, rather
/// than a child table. The character id (<c>uint</c>) maps to <c>bigint</c> since Postgres has no unsigned
/// integers; it is bound with a <c>long</c> cast and read back with a cast, the same value Mongo stores in
/// <c>_id</c>. Save is a single <c>INSERT … ON CONFLICT (character_id) DO UPDATE</c>, the equivalent of
/// <c>ReplaceOne(IsUpsert)</c>: the stored entries are replaced wholesale, never merged.
/// </remarks>
public sealed class MonsterCollectionProgressRepository : IMonsterCollectionProgressRepository
{
    public const string TableName = "collection_monster_progress";

    private readonly PostgresDbContext _db;

    public MonsterCollectionProgressRepository(PostgresDbContext db) => _db = db;

 /// <summary>One element of the stored <c>entries</c> jsonb array (a <see cref="MonsterCollectionEntry"/>).</summary>
    private sealed record EntryJson(uint NpcId, byte Level, uint Exp);

    public async Task<MonsterCollectionProgressDocument?> GetAsync(uint characterId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"SELECT entries FROM {TableName} WHERE character_id = @id", conn);
        cmd.Parameters.AddWithValue("id", (long)characterId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        var stored = JsonSerializer.Deserialize<List<EntryJson>>(reader.GetString(0)) ?? new List<EntryJson>();
        return new MonsterCollectionProgressDocument
        {
            Id = characterId,
            Entries = stored
                .Select(e => new MonsterCollectionEntry { NpcId = e.NpcId, Level = e.Level, Exp = e.Exp })
                .ToList(),
        };
    }

    public async Task SaveAsync(MonsterCollectionProgressDocument doc, CancellationToken ct = default)
    {
        var entries = (doc.Entries ?? new List<MonsterCollectionEntry>())
            .Select(e => new EntryJson(e.NpcId, e.Level, e.Exp))
            .ToList();

        await using var conn = await _db.OpenConnectionAsync(ct);
        
        await using var cmd = new NpgsqlCommand(
            $"INSERT INTO {TableName} (character_id, entries) VALUES (@id, @entries) " +
            "ON CONFLICT (character_id) DO UPDATE SET entries = EXCLUDED.entries",
            conn);
        cmd.Parameters.AddWithValue("id", (long)doc.Id);
        cmd.Parameters.AddWithValue("entries", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(entries));
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
