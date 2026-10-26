using Npgsql;
using RO2.Skills.Domain;
using RO2.Persistence.Postgres;
using NpgsqlTypes;

namespace RO2.Skills.Infrastructure.Postgres;

/// <summary>
/// PostgreSQL implementation of <see cref="ISkillRepository"/>: the learned-skill rows of each character in the
/// <c>character_skills</c> table. Mirrors the Mongo provider's <c>character_skills</c> collection one row per
/// document: <c>id</c> (the surrogate <see cref="CharacterSkill.Id"/>), <c>character_id</c>, <c>skill_id</c>
/// and <c>level</c>, with a unique (character_id, skill_id) constraint.
/// </summary>
/// <remarks>
/// <para>The <c>uint</c> character and skill ids and the <c>ulong</c> row id map to <c>bigint</c> (Postgres has no
/// unsigned integers) and the <c>byte</c> level maps to <c>smallint</c>; all are cast back on read.</para>
/// <para>The Mongo <c>LearnSkillAsync</c> is read-then-write (find, raise the level if higher, else insert with
/// <c>max(Id)+1</c>). Here it is one atomic statement: <c>INSERT … ON CONFLICT (character_id, skill_id) DO UPDATE
/// SET level = EXCLUDED.level WHERE character_skills.level &lt; EXCLUDED.level</c>, which is a no-op when the
/// skill is already learned at an equal or higher level, so a lower-level re-learn never downgrades. The row id
/// comes from an identity column instead of the racy <c>max+1</c>; it stays strictly ascending in insertion order,
/// which <see cref="GetSkillsAsync"/> uses (<c>ORDER BY id</c>) to match Mongo's natural order. Updating a skill that
/// is not learned changes nothing (no upsert), exactly as the Mongo <c>UpdateOneAsync</c>.</para>
/// <para><see cref="UpsertSkillsAsync"/> is one <c>INSERT … SELECT FROM unnest(…) ON CONFLICT (character_id, skill_id) DO
/// UPDATE SET level = EXCLUDED.level</c>. The unique (character_id, skill_id) constraint
/// (<c>ux_character_skills_char_skill</c>) already comes with the schema, so there is no index to create here.</para>
/// </remarks>
public sealed class SkillRepository : ISkillRepository
{
    public const string TableName = "character_skills";

    private readonly PostgresDbContext _db;

    public SkillRepository(PostgresDbContext db) => _db = db;

    public async Task<IReadOnlyList<CharacterSkill>> GetSkillsAsync(uint characterId)
    {
        await using var conn = await _db.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            $"SELECT id, character_id, skill_id, level FROM {TableName} WHERE character_id = @c ORDER BY id", conn);
        cmd.Parameters.AddWithValue("c", (long)characterId);
        await using var reader = await cmd.ExecuteReaderAsync();
        var result = new List<CharacterSkill>();
        while (await reader.ReadAsync())
        {
            result.Add(new CharacterSkill
            {
                Id = (ulong)reader.GetInt64(0),
                CharacterId = (uint)reader.GetInt64(1),
                SkillId = (uint)reader.GetInt64(2),
                Level = (byte)reader.GetInt16(3)
            });
        }
        return result;
    }

    public async Task LearnSkillAsync(uint characterId, uint skillId, byte level)
    {
        await using var conn = await _db.OpenConnectionAsync();
        // New skill: insert. Already learned: raise the level only if the new one is higher, else leave untouched.
        await using var cmd = new NpgsqlCommand(
            $"INSERT INTO {TableName} (character_id, skill_id, level) VALUES (@c, @s, @l) " +
            "ON CONFLICT (character_id, skill_id) DO UPDATE SET level = EXCLUDED.level " +
            $"WHERE {TableName}.level < EXCLUDED.level", conn);
        cmd.Parameters.AddWithValue("c", (long)characterId);
        cmd.Parameters.AddWithValue("s", (long)skillId);
        cmd.Parameters.AddWithValue("l", (short)level);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task UpdateSkillLevelAsync(uint characterId, uint skillId, byte newLevel)
    {
        await using var conn = await _db.OpenConnectionAsync();
        // Unconditional set on an existing row (may lower the level); no row => no-op, never an insert.
        await using var cmd = new NpgsqlCommand(
            $"UPDATE {TableName} SET level = @l WHERE character_id = @c AND skill_id = @s", conn);
        cmd.Parameters.AddWithValue("l", (short)newLevel);
        cmd.Parameters.AddWithValue("c", (long)characterId);
        cmd.Parameters.AddWithValue("s", (long)skillId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task UpsertSkillsAsync(uint characterId, IReadOnlyList<(uint SkillId, byte Level)> skills, CancellationToken ct = default)
    {
        if (skills.Count == 0) return;

        // A repeated id keeps its last level: ON CONFLICT DO UPDATE refuses to touch one row twice in a statement.
        var levels = new Dictionary<uint, byte>(skills.Count);
        foreach (var (skillId, level) in skills) levels[skillId] = level;
        var ids = new long[levels.Count];
        var lvls = new short[levels.Count];
        var i = 0;
        foreach (var (skillId, level) in levels) { ids[i] = skillId; lvls[i] = level; i++; }

        // ONE statement, so the whole batch is atomic (all rows or none). The unique (character_id, skill_id)
        // constraint of PostgresSchema is the conflict target; the level is set to exactly the requested one.
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"INSERT INTO {TableName} (character_id, skill_id, level) " +
            "SELECT @c, t.skill_id, t.level FROM unnest(@ids, @lvls) AS t(skill_id, level) " +
            "ON CONFLICT (character_id, skill_id) DO UPDATE SET level = EXCLUDED.level", conn);
        cmd.Parameters.AddWithValue("c", (long)characterId);
        cmd.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = ids });
        cmd.Parameters.Add(new NpgsqlParameter("lvls", NpgsqlDbType.Array | NpgsqlDbType.Smallint) { Value = lvls });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteSkillsAsync(uint characterId, IReadOnlyCollection<uint> skillIds, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"DELETE FROM {TableName} WHERE character_id = @c AND skill_id = ANY(@ids)", conn);
        cmd.Parameters.AddWithValue("c", (long)characterId);
        var ids = new long[skillIds.Count];
        var i = 0;
        foreach (var id in skillIds) ids[i++] = id;
        cmd.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = ids });
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
