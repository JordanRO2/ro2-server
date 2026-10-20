using System.Collections.Concurrent;
using Npgsql;
using NpgsqlTypes;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;

namespace RO2.Persistence.Postgres.Repositories;

/// <summary>
/// PostgreSQL implementation of <see cref="IGuildEmblemRepository"/>, persisting to the <c>guild_emblems</c>
/// table (the Mongo collection of the same name, keyed by the guild GUID as 32 lowercase hex chars). Mirrors the
/// Mongo provider's behaviour exactly: a DI singleton holding the whole registry in an in-memory map that is
/// loaded once by <see cref="InitAsync"/>; the reads (<see cref="Get"/>, <see cref="IndexOf"/>) are synchronous
/// cache lookups; before <see cref="InitAsync"/> the store is memory-only (so a unit test can exercise it
/// without a database); <see cref="SetAsync"/> computes the next index under a process-wide lock and then
/// upserts the whole row; <see cref="RemoveAsync"/> drops the cache entry first and deletes the row best effort.
/// </summary>
/// <remarks>
/// The aggregate is one flat row per guild with no nested list, so it maps to plain columns rather than
/// <c>jsonb</c>: the text <c>id</c> is the hex key (not a <c>uuid</c>, because the Mongo key is whatever
/// <c>Convert.ToHexStringLower</c> of the caller's bytes produced, and <see cref="SetAsync"/> does not validate
/// the length); the <c>ushort</c> index is <c>bigint</c> (Postgres has no unsigned integers); the image is
/// <c>bytea</c>; the stamp is <c>timestamptz</c>, normalised to UTC and truncated to whole milliseconds on
/// write (what a BSON date round-trip did) and read back with Kind=Utc. The next index is derived from the
/// in-memory map, exactly as in Mongo, so there is no database-side compare-and-set: the upsert is a single
/// <c>INSERT … ON CONFLICT DO UPDATE</c> statement, the equivalent of <c>ReplaceOne(IsUpsert)</c>.
/// </remarks>
public sealed class GuildEmblemRepository : IGuildEmblemRepository
{
    public const string TableName = "guild_emblems";

    private readonly ConcurrentDictionary<string, GuildEmblem> _byGuid = new();
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly Lock _gate = new();
    private readonly PostgresDbContext _db;
    private volatile bool _persist;
    private volatile bool _loaded;

    public GuildEmblemRepository(PostgresDbContext db) => _db = db;

 /// <summary>The row key for a 16-byte guild GUID: 32 lowercase hex chars.</summary>
    private static string KeyOf(ReadOnlySpan<byte> guid16) => Convert.ToHexStringLower(guid16);

    public async Task InitAsync()
    {
        if (_loaded) return;
        await _initLock.WaitAsync();
        try
        {
            if (_loaded) return;
            _persist = true;
            var rows = new List<GuildEmblem>();
            await using (var conn = await _db.OpenConnectionAsync())
            {
                await using var cmd = new NpgsqlCommand(
                    $"SELECT id, emblem_index, image, updated_utc FROM {TableName}", conn);
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    rows.Add(new GuildEmblem
                    {
                        Id = reader.GetString(0),
                        Index = (ushort)reader.GetInt64(1),
                        Image = reader.GetFieldValue<byte[]>(2),
                        UpdatedUtc = DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc),
                    });
                }
            }
            foreach (var e in rows)
                _byGuid[e.Id] = e;
            _loaded = true;
        }
        finally { _initLock.Release(); }
    }

    public GuildEmblem? Get(ReadOnlySpan<byte> guid16)
        => guid16.Length == 16 && guid16.IndexOfAnyExcept((byte)0) >= 0
           && _byGuid.TryGetValue(KeyOf(guid16), out var e) ? e : null;

    public ushort IndexOf(ReadOnlySpan<byte> guid16) => Get(guid16)?.Index ?? 0;

    public async Task<GuildEmblem> SetAsync(byte[] guid16, byte[] image)
    {
        var key = KeyOf(guid16);
        GuildEmblem emblem;
        lock (_gate)
        {
            var prev = _byGuid.TryGetValue(key, out var p) ? p.Index : (ushort)0;
            var next = (ushort)(prev + 1);
            if (next == 0) next = 1;
            emblem = new GuildEmblem { Id = key, Index = next, Image = image, UpdatedUtc = DateTime.UtcNow };
            _byGuid[key] = emblem;
        }
        if (_persist)
        {
            await using var conn = await _db.OpenConnectionAsync();
            await using var cmd = new NpgsqlCommand(
                $"INSERT INTO {TableName} (id, emblem_index, image, updated_utc) VALUES (@id, @idx, @image, @updated) " +
                "ON CONFLICT (id) DO UPDATE SET " +
                "emblem_index = EXCLUDED.emblem_index, image = EXCLUDED.image, updated_utc = EXCLUDED.updated_utc",
                conn);
            cmd.Parameters.AddWithValue("id", key);
            cmd.Parameters.Add(new NpgsqlParameter("idx", NpgsqlDbType.Bigint) { Value = (long)emblem.Index });
            cmd.Parameters.Add(new NpgsqlParameter("image", NpgsqlDbType.Bytea) { Value = emblem.Image });
            cmd.Parameters.Add(new NpgsqlParameter("updated", NpgsqlDbType.TimestampTz) { Value = ToUtcMilliseconds(emblem.UpdatedUtc) });
            await cmd.ExecuteNonQueryAsync();
        }
        return emblem;
    }

    public async Task RemoveAsync(byte[] guid16)
    {
        var key = KeyOf(guid16);
        _byGuid.TryRemove(key, out _);
        try
        {
            if (_persist)
            {
                await using var conn = await _db.OpenConnectionAsync();
                await using var cmd = new NpgsqlCommand($"DELETE FROM {TableName} WHERE id = @id", conn);
                cmd.Parameters.AddWithValue("id", key);
                await cmd.ExecuteNonQueryAsync();
            }
        }
        catch {   }
    }

    
    private static DateTime ToUtcMilliseconds(DateTime value)
    {
        var utc = value.ToUniversalTime();
        return new DateTime(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
    }
}
