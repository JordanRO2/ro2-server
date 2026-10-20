using Npgsql;
using NpgsqlTypes;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Domain.Enums;

namespace RO2.Persistence.Postgres.Repositories;

/// <summary>
/// PostgreSQL implementation of <see cref="IQuestRepository"/>: each character's quest runs in the
/// <c>character_quests</c> table, one row per run. The Mongo provider kept one <c>CharacterQuest</c> document
/// per run in the <c>character_quests</c> collection; here a row is that document, flattened (no nested part,
/// so no jsonb). A quest can be accepted again after completion (daily/repeat quests), and the Mongo code
/// also allowed a second in-progress run of the same quest, so there is deliberately NO uniqueness
/// constraint on (character_id, quest_id).
/// </summary>
/// <remarks>
/// <para>
/// <b>Types.</b> <c>uint</c> character and quest ids are stored as <c>bigint</c> (Postgres has no unsigned
/// integers) and cast back on read; the <c>ulong</c> document id is a <c>bigint</c> identity (it replaces the
/// Mongo "highest id + 1" scan, which was racy under concurrent accepts); <see cref="QuestStatus"/> is a
/// <c>smallint</c>; <c>started_at</c>/<c>completed_at</c> are <c>timestamptz</c> always written as UTC.
/// <c>ProgressJson</c> is an opaque string owned by the zone's progress store, so it is kept as <c>text</c>
/// (not jsonb) to round-trip byte-for-byte.
/// </para>
/// <para>
/// <b>Single-document updates.</b> Mongo's <c>UpdateOne</c>/<c>DeleteOne</c> touch ONE matching in-progress
/// document (the first in natural order). The equivalent here is
/// <c>WHERE id = (SELECT id … WHERE &lt;predicate&gt; ORDER BY id LIMIT 1) AND &lt;predicate&gt;</c>; the
/// predicate is repeated in the outer statement so that, under READ COMMITTED, a concurrent change that
/// already moved the chosen row out of <c>InProgress</c> makes the outer re-check fail and the statement
/// affect zero rows instead of overwriting it. Complete/give-up/save-progress therefore keep the
/// "only an in-progress document is touched" compare-and-set, and a no-match is a silent no-op as in Mongo.
/// </para>
/// <para>
/// <b>Tracer set.</b> The two Mongo <c>UpdateMany</c> calls (pin the requested, un-pin the rest) collapse
/// into one <c>UPDATE … SET is_traced = (quest_id = ANY(@ids))</c> over the character's in-progress rows,
/// which is also atomic. Completed rows are never touched.
/// </para>
/// </remarks>
public sealed class QuestRepository : IQuestRepository
{
    public const string TableName = "character_quests";

    private const short InProgress = (short)QuestStatus.InProgress;
    private const short Completed = (short)QuestStatus.Completed;

    private readonly PostgresDbContext _db;

    public QuestRepository(PostgresDbContext db) => _db = db;

    public async Task<IReadOnlyList<CharacterQuest>> GetActiveQuestsAsync(uint characterId)
    {
        await using var conn = await _db.OpenConnectionAsync();
        
        await using var cmd = new NpgsqlCommand(
            "SELECT id, quest_id, progress_json, is_traced, started_at, completed_at " +
            $"FROM {TableName} WHERE character_id = @c AND status = @s ORDER BY started_at, id", conn);
        cmd.Parameters.AddWithValue("c", (long)characterId);
        cmd.Parameters.AddWithValue("s", InProgress);
        await using var reader = await cmd.ExecuteReaderAsync();
        var quests = new List<CharacterQuest>();
        while (await reader.ReadAsync())
        {
            quests.Add(new CharacterQuest
            {
                Id = (ulong)reader.GetInt64(0),
                CharacterId = characterId,
                QuestId = (uint)reader.GetInt64(1),
                Status = QuestStatus.InProgress,
                ProgressJson = reader.IsDBNull(2) ? null : reader.GetString(2),
                IsTraced = reader.GetBoolean(3),
                StartedAt = reader.GetDateTime(4),
                CompletedAt = reader.IsDBNull(5) ? null : reader.GetDateTime(5),
            });
        }
        return quests;
    }

    public async Task<IReadOnlyList<uint>> GetCompletedQuestIdsAsync(uint characterId)
    {
        await using var conn = await _db.OpenConnectionAsync();
        
        await using var cmd = new NpgsqlCommand(
            $"SELECT quest_id FROM {TableName} WHERE character_id = @c AND status = @s ORDER BY id", conn);
        cmd.Parameters.AddWithValue("c", (long)characterId);
        cmd.Parameters.AddWithValue("s", Completed);
        await using var reader = await cmd.ExecuteReaderAsync();
        var ids = new List<uint>();
        while (await reader.ReadAsync())
            ids.Add((uint)reader.GetInt64(0));
        return ids;
    }

    public async Task<DateTime?> GetLastCompletedAtAsync(uint characterId, uint questId)
    {
        await using var conn = await _db.OpenConnectionAsync();
        
        await using var cmd = new NpgsqlCommand(
            $"SELECT MAX(completed_at) FROM {TableName} " +
            "WHERE character_id = @c AND quest_id = @q AND status = @s", conn);
        cmd.Parameters.AddWithValue("c", (long)characterId);
        cmd.Parameters.AddWithValue("q", (long)questId);
        cmd.Parameters.AddWithValue("s", Completed);
        var value = await cmd.ExecuteScalarAsync();
        return value is null or DBNull ? null : (DateTime)value;
    }

    public async Task AcceptQuestAsync(uint characterId, uint questId)
    {
        await using var conn = await _db.OpenConnectionAsync();
        
        await using var cmd = new NpgsqlCommand(
            $"INSERT INTO {TableName} (character_id, quest_id, status, is_traced, started_at) " +
            "VALUES (@c, @q, @s, false, @t)", conn);
        cmd.Parameters.AddWithValue("c", (long)characterId);
        cmd.Parameters.AddWithValue("q", (long)questId);
        cmd.Parameters.AddWithValue("s", InProgress);
        cmd.Parameters.AddWithValue("t", DateTime.UtcNow);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task CompleteQuestAsync(uint characterId, uint questId)
    {
        await using var conn = await _db.OpenConnectionAsync();
        
        
        await using var cmd = new NpgsqlCommand(
            $"UPDATE {TableName} SET status = @done, completed_at = @t WHERE id = (" +
            $"SELECT id FROM {TableName} WHERE character_id = @c AND quest_id = @q AND status = @s " +
            "ORDER BY id LIMIT 1) AND character_id = @c AND quest_id = @q AND status = @s", conn);
        cmd.Parameters.AddWithValue("done", Completed);
        cmd.Parameters.AddWithValue("t", DateTime.UtcNow);
        cmd.Parameters.AddWithValue("c", (long)characterId);
        cmd.Parameters.AddWithValue("q", (long)questId);
        cmd.Parameters.AddWithValue("s", InProgress);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task GiveUpQuestAsync(uint characterId, uint questId)
    {
        await using var conn = await _db.OpenConnectionAsync();
        
        await using var cmd = new NpgsqlCommand(
            $"DELETE FROM {TableName} WHERE id = (" +
            $"SELECT id FROM {TableName} WHERE character_id = @c AND quest_id = @q AND status = @s " +
            "ORDER BY id LIMIT 1) AND character_id = @c AND quest_id = @q AND status = @s", conn);
        cmd.Parameters.AddWithValue("c", (long)characterId);
        cmd.Parameters.AddWithValue("q", (long)questId);
        cmd.Parameters.AddWithValue("s", InProgress);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task SetQuestsTracedAsync(uint characterId, IReadOnlyCollection<uint> tracedQuestIds)
    {
        var ids = new long[tracedQuestIds.Count];
        var i = 0;
        foreach (var id in tracedQuestIds) ids[i++] = id;

        await using var conn = await _db.OpenConnectionAsync();
        
        
        await using var cmd = new NpgsqlCommand(
            $"UPDATE {TableName} SET is_traced = (quest_id = ANY(@ids)) " +
            "WHERE character_id = @c AND status = @s", conn);
        cmd.Parameters.AddWithValue("ids", ids);
        cmd.Parameters.AddWithValue("c", (long)characterId);
        cmd.Parameters.AddWithValue("s", InProgress);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task SaveProgressAsync(uint characterId, uint questId, string? progressJson)
    {
        await using var conn = await _db.OpenConnectionAsync();
        
        await using var cmd = new NpgsqlCommand(
            $"UPDATE {TableName} SET progress_json = @p WHERE id = (" +
            $"SELECT id FROM {TableName} WHERE character_id = @c AND quest_id = @q AND status = @s " +
            "ORDER BY id LIMIT 1) AND character_id = @c AND quest_id = @q AND status = @s", conn);
        cmd.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Text)
        {
            Value = progressJson is null ? DBNull.Value : progressJson,
        });
        cmd.Parameters.AddWithValue("c", (long)characterId);
        cmd.Parameters.AddWithValue("q", (long)questId);
        cmd.Parameters.AddWithValue("s", InProgress);
        await cmd.ExecuteNonQueryAsync();
    }
}
