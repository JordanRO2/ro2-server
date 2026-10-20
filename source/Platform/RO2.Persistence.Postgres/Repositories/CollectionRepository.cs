using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;

namespace RO2.Persistence.Postgres.Repositories;

/// <summary>
/// PostgreSQL implementation of <see cref="ICollectionRepository"/>: the per-character pet/monster
/// Collection book in the <c>collection_books</c> table, one row per character keyed by character id.
/// Mirrors the Mongo provider's behaviour exactly: <see cref="GetOrCreateAsync"/> returns the stored
/// document or a fresh in-memory default that is NOT written, and <see cref="SaveAsync"/> is a whole-document
/// upsert (create on first save, full replace afterwards).
/// </summary>
/// <remarks>
/// The document is a single aggregate that is only ever read and written whole, so it maps to one row with
/// the scalars as columns and the four id lists plus the pet-loyalty list as <c>jsonb</c> arrays (order and
/// duplicates preserved exactly as the Mongo arrays preserved them). No child table: nothing queries, pages
/// or compare-and-sets inside the lists. The <c>uint</c> character id and pet id map to <c>bigint</c>
/// (Postgres has no unsigned integers) and <c>ActivePetFlag</c> (a byte) to <c>smallint</c>. The Mongo upsert
/// (<c>ReplaceOne … IsUpsert</c>) is a single <c>INSERT … ON CONFLICT (character_id) DO UPDATE</c>, so two
/// concurrent first saves cannot fail with a duplicate key. As in Mongo, the stored <c>ActivePetFlag</c> is
/// returned verbatim; its re-derivation is the Zone service's job, not the repository's.
/// </remarks>
public sealed class CollectionRepository : ICollectionRepository
{
    public const string TableName = "collection_books";

    private readonly PostgresDbContext _db;

    public CollectionRepository(PostgresDbContext db) => _db = db;

    public async Task<CollectionDocument> GetOrCreateAsync(uint characterId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT registered_monster_ids, registered_pet_ids, selected_monster_skills, selected_pet_skills, " +
            $"active_pet_id, active_pet_flag, pet_loyalties FROM {TableName} WHERE character_id = @id", conn);
        cmd.Parameters.AddWithValue("id", (long)characterId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return new CollectionDocument { Id = characterId }; 
        return new CollectionDocument
        {
            Id = characterId,
            RegisteredMonsterIds = ReadList<uint>(reader.GetString(0)),
            RegisteredPetIds = ReadList<uint>(reader.GetString(1)),
            SelectedMonsterSkills = ReadList<uint>(reader.GetString(2)),
            SelectedPetSkills = ReadList<uint>(reader.GetString(3)),
            ActivePetId = (uint)reader.GetInt64(4),
            ActivePetFlag = (byte)reader.GetInt16(5),
            PetLoyalties = ReadList<PetLoyalty>(reader.GetString(6)),
        };
    }

    public async Task SaveAsync(CollectionDocument doc, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"INSERT INTO {TableName} (character_id, registered_monster_ids, registered_pet_ids, " +
            "selected_monster_skills, selected_pet_skills, active_pet_id, active_pet_flag, pet_loyalties) " +
            "VALUES (@id, @rm, @rp, @sm, @sp, @ap, @af, @pl) " +
            "ON CONFLICT (character_id) DO UPDATE SET " +
            "registered_monster_ids = EXCLUDED.registered_monster_ids, " +
            "registered_pet_ids = EXCLUDED.registered_pet_ids, " +
            "selected_monster_skills = EXCLUDED.selected_monster_skills, " +
            "selected_pet_skills = EXCLUDED.selected_pet_skills, " +
            "active_pet_id = EXCLUDED.active_pet_id, " +
            "active_pet_flag = EXCLUDED.active_pet_flag, " +
            "pet_loyalties = EXCLUDED.pet_loyalties",
            conn);
        cmd.Parameters.AddWithValue("id", (long)doc.Id);
        cmd.Parameters.AddWithValue("rm", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(doc.RegisteredMonsterIds));
        cmd.Parameters.AddWithValue("rp", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(doc.RegisteredPetIds));
        cmd.Parameters.AddWithValue("sm", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(doc.SelectedMonsterSkills));
        cmd.Parameters.AddWithValue("sp", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(doc.SelectedPetSkills));
        cmd.Parameters.AddWithValue("ap", (long)doc.ActivePetId);
        cmd.Parameters.AddWithValue("af", (short)doc.ActivePetFlag);
        cmd.Parameters.AddWithValue("pl", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(doc.PetLoyalties));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static List<T> ReadList<T>(string json) => JsonSerializer.Deserialize<List<T>>(json) ?? new List<T>();
}
