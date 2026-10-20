using System.Buffers.Binary;
using System.Collections.Concurrent;
using Npgsql;
using NpgsqlTypes;
using RO2.Application.Interfaces;

namespace RO2.Persistence.Postgres.Repositories;

/// <summary>
/// PostgreSQL implementation of <see cref="IGuildDepartureRepository"/>, persisting per-character departure
/// state to the <c>guild_departures</c> table so a rejoin cooldown and an offline-kick notice survive a Zone
/// restart (which also closes every client, so the notice would otherwise be lost). Mirrors the Mongo
/// provider's behaviour exactly: the state is loaded into an in-memory map once on <see cref="InitAsync"/>,
/// reads are synchronous against that map, every change is applied to the map under one lock first and then
/// written through as a whole-row upsert (the SQL equivalent of <c>ReplaceOne(IsUpsert)</c>), and the write is
/// best effort — a database failure never fails a handler. A DI singleton over the process-wide
/// <see cref="PostgresDbContext"/>; before <see cref="InitAsync"/> has loaded successfully there is no
/// persistence and the store is memory-only (so a unit test can exercise it without a database).
/// </summary>
/// <remarks>
/// One flat row per character: <c>char_id</c> (the <c>uint</c> character id, <c>bigint</c> because Postgres has
/// no unsigned integers) is the primary key, <c>left_utc</c> is a nullable <c>timestamptz</c> and
/// <c>kick_notice_pending</c> a <c>boolean</c>. The two fields are independent (recording a leave keeps a
/// pending notice, and taking the notice keeps the leave time), exactly as in the Mongo document. The
/// "take the notice once" compare-and-set is the in-process check-and-clear under the lock, as in Mongo: the
/// map is the single authority for a one-process singleton, so there is no database-side CAS and no
/// <c>UPDATE … RETURNING</c>. Instants are written as Kind=Utc truncated to whole milliseconds, which is what a
/// BSON date round-trip did, and read back with Kind=Utc.
/// </remarks>
public sealed class GuildDepartureRepository : IGuildDepartureRepository
{
    public const string TableName = "guild_departures";

    /// <summary>One character's departure state.</summary>
    private sealed class GuildDeparture
    {
        public uint CharId { get; set; }
        public DateTime? LeftUtc { get; set; }
        public bool KickNoticePending { get; set; }
    }

    private readonly ConcurrentDictionary<uint, GuildDeparture> _byChar = new();
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly Lock _gate = new();
    private readonly PostgresDbContext _db;
    private volatile bool _persistent;
    private volatile bool _loaded;

    public GuildDepartureRepository(PostgresDbContext db) => _db = db;

    // Column ordinals of the SELECT in InitAsync (the order of its column list).
    private const int ColumnCharId = 0;
    private const int ColumnLeftUtc = 1;
    private const int ColumnKickNoticePending = 2;

    /// <summary>
    /// Inner length of the 2570 kick-notice body: <c>3 + 2n</c> for a name of n = 1 character (the u8 name
    /// length, the one UTF-16 character, the u16 terminator).
    /// </summary>
    private const uint KickNoticeInnerLength = 5;

    /// <summary>Name length (in UTF-16 characters) of the placeholder name in the kick-notice body.</summary>
    private const byte KickNoticeNameChars = 1;

    /// <summary>
    /// The single placeholder name character (U+0012) of the kick-notice body. It is not a guild name: the
    /// client ignores the name and prints the expelled notice with the player's own name.
    /// </summary>
    private const ushort KickNoticePlaceholderChar = 0x0012;

    /// <summary>Size of the kick-notice body: u32 length + u8 name length + 1 UTF-16 char + u16 terminator.</summary>
    private const int KickNoticeBodyLength = 4 + 1 + 2 + 2;

    /// <summary>
    /// The 2570 NfyGuildKickMember body sent to a member kicked while offline, at their next world
    /// entry. It has the layout of the online kick notice, <c>[u32 L = 3 + 2n][u8 n][n UTF-16LE characters]
    /// [u16 0]</c>, with a one-character placeholder name instead of the guild name (9 bytes in all). The
    /// client ignores the name and shows the expelled notice with the player's own name.
    /// </summary>
    public static byte[] KickNoticeBody()
    {
        var body = new byte[KickNoticeBodyLength];
        BinaryPrimitives.WriteUInt32LittleEndian(body, KickNoticeInnerLength);
        body[4] = KickNoticeNameChars;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(5), KickNoticePlaceholderChar);
        // The trailing u16 terminator is the zero-initialised last two bytes.
        return body;
    }

    public async Task InitAsync()
    {
        if (_loaded) return;
        await _initLock.WaitAsync();
        try
        {
            if (_loaded) return;
            await using var conn = await _db.OpenConnectionAsync();
            await using var cmd = new NpgsqlCommand(
                $"SELECT char_id, left_utc, kick_notice_pending FROM {TableName}", conn);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var charId = (uint)reader.GetInt64(ColumnCharId);
                _byChar[charId] = new GuildDeparture
                {
                    CharId = charId,
                    LeftUtc = reader.IsDBNull(ColumnLeftUtc) ? null : reader.GetDateTime(ColumnLeftUtc),
                    KickNoticePending = reader.GetBoolean(ColumnKickNoticePending),
                };
            }
            _persistent = true;
            _loaded = true;
        }
        finally { _initLock.Release(); }
    }

    public DateTime? LeftUtcOf(uint charId)
    {
        lock (_gate) return _byChar.TryGetValue(charId, out var d) ? d.LeftUtc : null;
    }

    public Task RecordLeaveAsync(uint charId, DateTime whenUtc) => UpdateAsync(charId, d => d.LeftUtc = whenUtc);

    public Task MarkKickNoticeAsync(uint charId) => UpdateAsync(charId, d => d.KickNoticePending = true);

    public async Task<bool> TakeKickNoticeAsync(uint charId)
    {
        GuildDeparture snapshot;
        lock (_gate)
        {
            if (!_byChar.TryGetValue(charId, out var d) || !d.KickNoticePending) return false;
            d.KickNoticePending = false;
            snapshot = Copy(d);
        }
        await PersistAsync(snapshot);
        return true;
    }

    private async Task UpdateAsync(uint charId, Action<GuildDeparture> change)
    {
        GuildDeparture snapshot;
        lock (_gate)
        {
            var d = _byChar.GetOrAdd(charId, id => new GuildDeparture { CharId = id });
            change(d);
            snapshot = Copy(d);
        }
        await PersistAsync(snapshot);
    }

    private static GuildDeparture Copy(GuildDeparture d)
        => new() { CharId = d.CharId, LeftUtc = d.LeftUtc, KickNoticePending = d.KickNoticePending };

    private async Task PersistAsync(GuildDeparture d)
    {
        // Best effort: the live state is already updated, and a handler must not fail on a write. Before a
        // successful InitAsync (tests) there is no persistence and the store is memory-only.
        try
        {
            if (!_persistent) return;
            await using var conn = await _db.OpenConnectionAsync();
            await using var cmd = new NpgsqlCommand(
                $"INSERT INTO {TableName} (char_id, left_utc, kick_notice_pending) VALUES (@id, @left, @kick) " +
                "ON CONFLICT (char_id) DO UPDATE SET " +
                "left_utc = EXCLUDED.left_utc, kick_notice_pending = EXCLUDED.kick_notice_pending",
                conn);
            cmd.Parameters.AddWithValue("id", (long)d.CharId);
            cmd.Parameters.Add(new NpgsqlParameter("left", NpgsqlDbType.TimestampTz)
            {
                Value = d.LeftUtc is { } v ? ToUtcMilliseconds(v) : DBNull.Value,
            });
            cmd.Parameters.AddWithValue("kick", d.KickNoticePending);
            await cmd.ExecuteNonQueryAsync();
        }
        catch { /* best effort */ }
    }

    // Kind=Utc, whole milliseconds: what a BSON date stores and returns.
    private static DateTime ToUtcMilliseconds(DateTime value)
    {
        var utc = value.ToUniversalTime();
        return new DateTime(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
    }
}
