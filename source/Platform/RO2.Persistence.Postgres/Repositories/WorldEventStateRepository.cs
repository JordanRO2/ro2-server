using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using RO2.Worlds.Application;
using RO2.Worlds.Domain;

namespace RO2.Persistence.Postgres.Repositories;

/// <summary>
/// PostgreSQL implementation of <see cref="IWorldEventStateRepository"/>: the world events' server-wide live
/// state, one row per event in the <c>world_event_state</c> table keyed by World_Event_ID. Mirrors the Mongo
/// provider's behaviour exactly - raw document I/O (errors propagate), <see cref="GetAllAsync"/> returns every
/// stored event, and <see cref="SaveAsync"/> replaces one event's whole state, creating it on first save (the
/// SQL equivalent of <c>ReplaceOne(IsUpsert)</c>).
/// </summary>
/// <remarks>
/// The per-row offering counts are never queried, paged or compare-and-set on their own (the interface only
/// lists everything and replaces a whole document), so they are one <c>jsonb</c> array rather than a child
/// table; <c>jsonb</c> preserves array order, which is the WorldEvent_Check_Group INDEX order the world-event
/// service relies on. The <c>uint</c> event id (Mongo <c>_id</c>) maps to <c>bigint</c> (Postgres has no
/// unsigned integers) and is read back with a cast; the <c>byte</c> stage maps to <c>smallint</c>; the
/// <c>uint</c> counts are JSON numbers. Both timestamps are <c>timestamptz</c>, normalised to UTC and truncated
/// to whole milliseconds on write, which is what a BSON date round-trip did, and they read back with
/// Kind=Utc. A save is a single <c>INSERT … ON CONFLICT DO UPDATE</c>: the stored row is replaced wholesale,
/// never merged, and concurrent saves of the same event are last-write-wins as in Mongo.
/// </remarks>
public sealed class WorldEventStateRepository : IWorldEventStateRepository
{
    public const string TableName = "world_event_state";

    private readonly PostgresDbContext _db;

    public WorldEventStateRepository(PostgresDbContext db) => _db = db;

    public async Task<IReadOnlyList<WorldEventStateDocument>> GetAllAsync(CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        
        await using var cmd = new NpgsqlCommand(
            $"SELECT world_event_id, stage, stage_entered_utc, counts, updated_utc FROM {TableName} " +
            "ORDER BY world_event_id",
            conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var result = new List<WorldEventStateDocument>();
        while (await reader.ReadAsync(ct))
        {
            result.Add(new WorldEventStateDocument
            {
                Id = (uint)reader.GetInt64(0),
                Stage = (byte)reader.GetInt16(1),
                StageEnteredUtc = DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc),
                Counts = JsonSerializer.Deserialize<List<uint>>(reader.GetString(3)) ?? new List<uint>(),
                UpdatedUtc = DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc),
            });
        }
        return result;
    }

    public async Task SaveAsync(WorldEventStateDocument doc, CancellationToken ct = default)
    {
        var counts = doc.Counts ?? new List<uint>();

        await using var conn = await _db.OpenConnectionAsync(ct);
        
        await using var cmd = new NpgsqlCommand(
            $"INSERT INTO {TableName} (world_event_id, stage, stage_entered_utc, counts, updated_utc) " +
            "VALUES (@id, @stage, @entered, @counts, @updated) " +
            "ON CONFLICT (world_event_id) DO UPDATE SET stage = EXCLUDED.stage, " +
            "stage_entered_utc = EXCLUDED.stage_entered_utc, counts = EXCLUDED.counts, " +
            "updated_utc = EXCLUDED.updated_utc",
            conn);
        cmd.Parameters.AddWithValue("id", (long)doc.Id);
        cmd.Parameters.AddWithValue("stage", NpgsqlDbType.Smallint, (short)doc.Stage);
        cmd.Parameters.AddWithValue("entered", NpgsqlDbType.TimestampTz, ToUtcMilliseconds(doc.StageEnteredUtc));
        cmd.Parameters.AddWithValue("counts", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(counts));
        cmd.Parameters.AddWithValue("updated", NpgsqlDbType.TimestampTz, ToUtcMilliseconds(doc.UpdatedUtc));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    
    
    
    private static DateTime ToUtcMilliseconds(DateTime value)
    {
        if (value == DateTime.MinValue) return DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);
        var utc = value.ToUniversalTime();
        return new DateTime(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
    }
}
