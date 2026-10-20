using Npgsql;
using NpgsqlTypes;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;

namespace RO2.Persistence.Postgres.Repositories;

/// <summary>
/// PostgreSQL implementation of <see cref="IKharaTitleRepository"/>: one flat row per character in the
/// <c>character_khara_title</c> table (the Mongo collection of the same name, keyed by character id). Mirrors
/// the Mongo provider's behaviour exactly: <see cref="GetAsync"/> returns null when no row exists,
/// <see cref="SaveAsync"/> replaces the whole document (every column, including nullable instants going back
/// to null) and creates it on first save, and <see cref="StorePlayClockAsync"/> updates only the play clock and
/// the updated stamp of an existing row and never creates one.
/// </summary>
/// <remarks>
/// The document is eight scalars with no nested list, so it maps to plain columns rather than <c>jsonb</c>.
/// The <c>uint</c> character id and the two Khara ids map to <c>bigint</c> (Postgres has no unsigned
/// integers). The four instants are <c>timestamptz</c>; the three nullable ones are SQL NULL when the document
/// value is null. Every instant is normalised to UTC and truncated to whole milliseconds on write, which is what
/// a BSON date round-trip did, and reads back with Kind=Utc. The save is a single
/// <c>INSERT … ON CONFLICT DO UPDATE</c> (the SQL equivalent of <c>ReplaceOne(IsUpsert)</c>) and the play-clock
/// store is a single <c>UPDATE … WHERE character_id = @id</c> (the equivalent of <c>UpdateOne</c> without upsert).
/// </remarks>
public sealed class KharaTitleRepository : IKharaTitleRepository
{
    public const string TableName = "character_khara_title";

    private readonly PostgresDbContext _db;

    public KharaTitleRepository(PostgresDbContext db) => _db = db;

    public async Task<KharaTitleDocument?> GetAsync(uint characterId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT khara_a, khara_b, a_timer_start_utc, last_success_utc, play_clock_utc, updated_utc " +
            $"FROM {TableName} WHERE character_id = @id", conn);
        cmd.Parameters.AddWithValue("id", (long)characterId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;              

        return new KharaTitleDocument
        {
            Id = characterId,
            KharaA = (uint)reader.GetInt64(0),
            KharaB = (uint)reader.GetInt64(1),
            ATimerStartUtc = reader.IsDBNull(2) ? null : reader.GetDateTime(2),
            LastSuccessUtc = reader.IsDBNull(3) ? null : reader.GetDateTime(3),
            PlayClockUtc = reader.IsDBNull(4) ? null : reader.GetDateTime(4),
            UpdatedUtc = reader.GetDateTime(5),
        };
    }

    public async Task SaveAsync(KharaTitleDocument doc, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"INSERT INTO {TableName} " +
            "(character_id, khara_a, khara_b, a_timer_start_utc, last_success_utc, play_clock_utc, updated_utc) " +
            "VALUES (@id, @a, @b, @timer, @success, @clock, @updated) " +
            "ON CONFLICT (character_id) DO UPDATE SET " +
            "khara_a = EXCLUDED.khara_a, khara_b = EXCLUDED.khara_b, " +
            "a_timer_start_utc = EXCLUDED.a_timer_start_utc, last_success_utc = EXCLUDED.last_success_utc, " +
            "play_clock_utc = EXCLUDED.play_clock_utc, updated_utc = EXCLUDED.updated_utc",
            conn);
        cmd.Parameters.AddWithValue("id", (long)doc.Id);
        cmd.Parameters.AddWithValue("a", (long)doc.KharaA);
        cmd.Parameters.AddWithValue("b", (long)doc.KharaB);
        AddInstant(cmd, "timer", doc.ATimerStartUtc);
        AddInstant(cmd, "success", doc.LastSuccessUtc);
        AddInstant(cmd, "clock", doc.PlayClockUtc);
        AddInstant(cmd, "updated", doc.UpdatedUtc);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task StorePlayClockAsync(uint characterId, DateTime playClockUtc, DateTime updatedUtc, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        
        await using var cmd = new NpgsqlCommand(
            $"UPDATE {TableName} SET play_clock_utc = @clock, updated_utc = @updated WHERE character_id = @id", conn);
        cmd.Parameters.AddWithValue("id", (long)characterId);
        AddInstant(cmd, "clock", playClockUtc);
        AddInstant(cmd, "updated", updatedUtc);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static void AddInstant(NpgsqlCommand cmd, string name, DateTime? value)
        => cmd.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.TimestampTz)
        {
            Value = value is { } v ? ToUtcMilliseconds(v) : DBNull.Value,
        });

    
    private static DateTime ToUtcMilliseconds(DateTime value)
    {
        var utc = value.ToUniversalTime();
        return new DateTime(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
    }
}
