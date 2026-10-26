using Npgsql;
using NpgsqlTypes;
using RO2.Worlds.Application;
using RO2.Worlds.Domain;

namespace RO2.Persistence.Postgres.Repositories;

/// <summary>
/// PostgreSQL implementation of <see cref="IIllusionPyramidStateRepository"/>: one flat row per character in the
/// <c>character_illusion_pyramid</c> table (the Mongo collection of the same name, keyed by character id).
/// Mirrors the Mongo provider's behaviour exactly: <see cref="GetAsync"/> returns a zero document carrying the
/// requested id (never null, never persisted) when the character has no row, and <see cref="SaveAsync"/> stamps
/// <c>UpdatedUtc</c> with the current UTC time on the caller's document, then replaces the whole row and creates
/// it on first save.
/// </summary>
/// <remarks>
/// The document is five scalars with no nested list, so it maps to plain columns rather than <c>jsonb</c>. The
/// <c>uint</c> character id maps to <c>bigint</c> (Postgres has no unsigned integers); the two balances are
/// <c>integer</c> (same range as the C# <c>int</c>, negatives preserved) and the <c>byte</c> top record is
/// <c>smallint</c>. <c>updated_utc</c> is <c>timestamptz</c>, written as Kind=Utc truncated to whole milliseconds
/// (what a BSON date round-trip did) and read back with Kind=Utc. The save is a single
/// <c>INSERT … ON CONFLICT DO UPDATE</c>, the SQL equivalent of <c>ReplaceOne(IsUpsert)</c>; there is no
/// compare-and-set because the interface has none (the Zone service serialises writers under its run lock).
/// </remarks>
public sealed class IllusionPyramidStateRepository : IIllusionPyramidStateRepository
{
    public const string TableName = "character_illusion_pyramid";

    private readonly PostgresDbContext _db;

    public IllusionPyramidStateRepository(PostgresDbContext db) => _db = db;

    public async Task<IllusionPyramidStateDocument> GetAsync(uint characterId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"SELECT mystical, bright, top_record, updated_utc FROM {TableName} WHERE character_id = @id", conn);
        cmd.Parameters.AddWithValue("id", (long)characterId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return new IllusionPyramidStateDocument { Id = characterId };   

        return new IllusionPyramidStateDocument
        {
            Id = characterId,
            Mystical = reader.GetInt32(0),
            Bright = reader.GetInt32(1),
            TopRecord = (byte)reader.GetInt16(2),
            UpdatedUtc = DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc),
        };
    }

    public async Task SaveAsync(IllusionPyramidStateDocument doc, CancellationToken ct = default)
    {
        doc.UpdatedUtc = DateTime.UtcNow;
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"INSERT INTO {TableName} (character_id, mystical, bright, top_record, updated_utc) " +
            "VALUES (@id, @mystical, @bright, @top, @updated) " +
            "ON CONFLICT (character_id) DO UPDATE SET " +
            "mystical = EXCLUDED.mystical, bright = EXCLUDED.bright, " +
            "top_record = EXCLUDED.top_record, updated_utc = EXCLUDED.updated_utc",
            conn);
        cmd.Parameters.AddWithValue("id", (long)doc.Id);
        cmd.Parameters.AddWithValue("mystical", doc.Mystical);
        cmd.Parameters.AddWithValue("bright", doc.Bright);
        cmd.Parameters.Add(new NpgsqlParameter("top", NpgsqlDbType.Smallint) { Value = (short)doc.TopRecord });
        cmd.Parameters.Add(new NpgsqlParameter("updated", NpgsqlDbType.TimestampTz)
        {
            Value = ToUtcMilliseconds(doc.UpdatedUtc),
        });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    
    private static DateTime ToUtcMilliseconds(DateTime value)
    {
        var utc = value.ToUniversalTime();
        return new DateTime(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
    }
}
