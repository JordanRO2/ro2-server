using System.Globalization;
using Npgsql;
using RO2.Application.Interfaces;

namespace RO2.Persistence.Postgres.Repositories;

/// <summary>
/// PostgreSQL implementation of <see cref="IDungeonEntryCountRepository"/>: one row per character in the
/// <c>character_dungeon_entries</c> table, mirroring the Mongo provider's one-document-per-character shape.
/// The row carries the start of the daily window the counts belong to and the per-DungeonID counts as a
/// <c>jsonb</c> object keyed by the id in decimal. The counts of an earlier window read as zero (reset
/// lazily, no timer), and an entry recorded in a different window starts a fresh object.
/// </summary>
/// <remarks>
/// The daily reset instant is 10:00 UTC, a measured constant (no table holds it); <see cref="WindowStart"/>
/// is the same computation the Mongo provider uses. The <c>uint</c> character id maps to <c>bigint</c>
/// (Postgres has no unsigned integers) and is the primary key, the way it was the Mongo <c>_id</c>; the
/// DungeonID is only ever a jsonb key (its decimal text), exactly as it is a document key in Mongo.
/// <para>
/// <see cref="RecordEntryAsync"/> is one atomic <c>INSERT … ON CONFLICT DO UPDATE … RETURNING</c> instead of
/// the Mongo read-modify-write <c>ReplaceOne</c>: same observable result for a serial caller, and two
/// concurrent entries of one character can no longer lose a count. The window is compared to the stored
/// one inside the statement, so a stale or later window replaces the whole counts object, as the Mongo
/// path replaces the whole document.
/// </para>
/// </remarks>
public sealed class DungeonEntryCountRepository : IDungeonEntryCountRepository
{
    public const string TableName = "character_dungeon_entries";

 /// <summary>Retail's daily reset instant as a UTC time of day.</summary>
    public static readonly TimeSpan ResetTimeOfDayUtc = TimeSpan.FromHours(10);

    private readonly PostgresDbContext _db;

    public DungeonEntryCountRepository(PostgresDbContext db) => _db = db;

 /// <summary>The start of the daily window <paramref name="utcNow"/> falls in: the latest 10:00 UTC at or before it.</summary>
    public static DateTime WindowStart(DateTime utcNow)
    {
        var utc = utcNow.Kind == DateTimeKind.Local ? utcNow.ToUniversalTime() : DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
        var today = DateTime.SpecifyKind(utc.Date, DateTimeKind.Utc) + ResetTimeOfDayUtc;
        return utc >= today ? today : today.AddDays(-1);
    }

    public async Task<int> GetUsedAsync(uint characterId, uint dungeonId, DateTime utcNow, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        
        await using var cmd = new NpgsqlCommand(
            $"SELECT (counts ->> @k::text)::integer FROM {TableName} WHERE character_id = @c AND window_start_utc = @w",
            conn);
        cmd.Parameters.AddWithValue("c", (long)characterId);
        cmd.Parameters.AddWithValue("w", WindowStart(utcNow));
        cmd.Parameters.AddWithValue("k", Key(dungeonId));
        var scalar = await cmd.ExecuteScalarAsync(ct);
        return scalar is int used ? used : 0;
    }

    public async Task<int> RecordEntryAsync(uint characterId, uint dungeonId, DateTime utcNow, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        
        
        await using var cmd = new NpgsqlCommand(
            $"INSERT INTO {TableName} AS e (character_id, window_start_utc, counts, updated_utc) " +
            "VALUES (@c, @w, jsonb_build_object(@k::text, 1), @u) " +
            "ON CONFLICT (character_id) DO UPDATE SET " +
            "counts = CASE WHEN e.window_start_utc = EXCLUDED.window_start_utc " +
            "THEN jsonb_set(e.counts, ARRAY[@k::text], to_jsonb(COALESCE((e.counts ->> @k::text)::integer, 0) + 1)) " +
            "ELSE EXCLUDED.counts END, " +
            "window_start_utc = EXCLUDED.window_start_utc, " +
            "updated_utc = EXCLUDED.updated_utc " +
            "RETURNING (e.counts ->> @k::text)::integer",
            conn);
        cmd.Parameters.AddWithValue("c", (long)characterId);
        cmd.Parameters.AddWithValue("w", WindowStart(utcNow));
        cmd.Parameters.AddWithValue("k", Key(dungeonId));
        cmd.Parameters.AddWithValue("u", DateTime.UtcNow);
        var count = await cmd.ExecuteScalarAsync(ct);
        return (int)count!;
    }

    private static string Key(uint dungeonId) => dungeonId.ToString(CultureInfo.InvariantCulture);
}
