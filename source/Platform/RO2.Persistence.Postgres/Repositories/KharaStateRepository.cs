using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;

namespace RO2.Persistence.Postgres.Repositories;

/// <summary>
/// PostgreSQL implementation of <see cref="IKharaStateRepository"/>: one row per character in the
/// <c>character_khara</c> table, keyed by character id. Mirrors the Mongo provider's behaviour exactly - raw
/// document I/O (errors propagate), a null from <see cref="GetAsync"/> when the character never saved a state,
/// and a whole-document upsert on <see cref="SaveAsync"/> (the SQL equivalent of <c>ReplaceOne(IsUpsert)</c>).
/// </summary>
/// <remarks>
/// The accepted-Khara list and the completed-id list are never queried, paged or compare-and-set on their own
/// (the interface only gets and replaces the whole document), so each is one <c>jsonb</c> array rather than a
/// child table; <c>jsonb</c> preserves array order, which is the ascending-by-id order and the per-condition-row
/// order of the progress counts that the Zone service relies on. The <c>uint</c> character id (Mongo
/// <c>_id</c>) and the <c>uint</c> point balance map to <c>bigint</c> (Postgres has no unsigned integers) and
/// are read back with a cast; the <c>uint</c> ids, states and counts inside the arrays are JSON numbers.
/// <see cref="KharaStateDocument.UpdatedUtc"/> is a <c>timestamptz</c>, normalised to UTC and truncated to whole
/// milliseconds on write, which is what a BSON date round-trip did, and it reads back with Kind=Utc. A save is
/// a single <c>INSERT … ON CONFLICT DO UPDATE</c>: the stored row is replaced wholesale, never merged.
/// </remarks>
public sealed class KharaStateRepository : IKharaStateRepository
{
    public const string TableName = "character_khara";

    private readonly PostgresDbContext _db;

    public KharaStateRepository(PostgresDbContext db) => _db = db;

 /// <summary>One element of the stored <c>active</c> jsonb array (a <see cref="KharaActiveDocument"/>).</summary>
    private sealed record ActiveJson(uint KharaId, uint State, List<uint> Counts);

    public async Task<KharaStateDocument?> GetAsync(uint characterId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"SELECT points, active, completed, updated_utc FROM {TableName} WHERE character_id = @id", conn);
        cmd.Parameters.AddWithValue("id", (long)characterId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;            

        var active = JsonSerializer.Deserialize<List<ActiveJson>>(reader.GetString(1)) ?? new List<ActiveJson>();
        var completed = JsonSerializer.Deserialize<List<uint>>(reader.GetString(2)) ?? new List<uint>();
        return new KharaStateDocument
        {
            Id = characterId,
            Points = (uint)reader.GetInt64(0),
            Active = active
                .Select(a => new KharaActiveDocument
                {
                    KharaId = a.KharaId,
                    State = a.State,
                    Counts = a.Counts ?? new List<uint>(),
                })
                .ToList(),
            Completed = completed,
            UpdatedUtc = DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc),
        };
    }

    public async Task SaveAsync(KharaStateDocument doc, CancellationToken ct = default)
    {
        var active = (doc.Active ?? new List<KharaActiveDocument>())
            .Select(a => new ActiveJson(a.KharaId, a.State, a.Counts ?? new List<uint>()))
            .ToList();
        var completed = doc.Completed ?? new List<uint>();

        await using var conn = await _db.OpenConnectionAsync(ct);
        
        await using var cmd = new NpgsqlCommand(
            $"INSERT INTO {TableName} (character_id, points, active, completed, updated_utc) " +
            "VALUES (@id, @points, @active, @completed, @updated) " +
            "ON CONFLICT (character_id) DO UPDATE SET points = EXCLUDED.points, active = EXCLUDED.active, " +
            "completed = EXCLUDED.completed, updated_utc = EXCLUDED.updated_utc",
            conn);
        cmd.Parameters.AddWithValue("id", (long)doc.Id);
        cmd.Parameters.AddWithValue("points", (long)doc.Points);
        cmd.Parameters.AddWithValue("active", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(active));
        cmd.Parameters.AddWithValue("completed", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(completed));
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
