using System.Collections.Concurrent;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using RO2.Application.Interfaces;
using RO2.Persistence.Mongo.Persistence;

namespace RO2.Persistence.Mongo.Repositories;

/// <summary>
/// MongoDB implementation of <see cref="IGuildDepartureRepository"/>, persisting per-character departure
/// state to the <c>guild_departures</c> collection so a rejoin cooldown and an offline-kick notice survive a
/// Zone restart (which also closes every client, so the notice would otherwise be lost). The state is loaded
/// into an in-memory map once on <see cref="InitAsync"/> and kept apart from the guild document because it
/// belongs to a character that is no longer a member of any guild. A DI singleton over the process-wide
/// <see cref="MongoDbContext"/>; before <see cref="InitAsync"/> there is no collection and the store is
/// memory-only (so a unit test can exercise it without a database).
/// </summary>
public sealed class GuildDepartureRepository : IGuildDepartureRepository
{
 /// <summary>One character's departure state. <c>CharId</c> maps to Mongo <c>_id</c>.</summary>
    private sealed class GuildDeparture
    {
        [BsonId] public uint CharId { get; set; }
        public DateTime? LeftUtc { get; set; }
        public bool KickNoticePending { get; set; }
    }

    private readonly ConcurrentDictionary<uint, GuildDeparture> _byChar = new();
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly Lock _gate = new();
    private readonly MongoDbContext _context;
    private IMongoCollection<GuildDeparture>? _collection;
    private volatile bool _loaded;

    public GuildDepartureRepository(MongoDbContext context) => _context = context;

    /// <summary>
    /// The 2570 NfyGuildKickMember body sent to a member kicked while offline, at their next world entry.
    /// It uses the same layout as the named form, <c>[u32 L = 3 + 2n][u8 n][n UTF-16LE chars][u16 0]</c>
    /// (a length-prefixed blob holding a WStr8 guild name and an empty WStr16), but with a fixed one-character
    /// placeholder name (n = 1, U+0012), 9 bytes in all. It carries no real guild name; the client ignores
    /// the body and shows the expelled notice with the player's own name.
    /// </summary>
    public static byte[] KickNoticeBody() =>
    [
        0x05, 0x00, 0x00, 0x00, // u32 L = 3 + 2n, with n = 1
        0x01,                   // u8 n: one UTF-16LE character follows
        0x12, 0x00,             // the placeholder "name" character, U+0012
        0x00, 0x00,             // u16 0: empty trailing WStr16
    ];

    public async Task InitAsync()
    {
        if (_loaded) return;
        await _initLock.WaitAsync();
        try
        {
            if (_loaded) return;
            _collection = _context.GetCollection<GuildDeparture>("guild_departures");
            foreach (var d in await _collection.Find(FilterDefinition<GuildDeparture>.Empty).ToListAsync())
                _byChar[d.CharId] = d;
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
        
        
        try
        {
            if (_collection != null)
                await _collection.ReplaceOneAsync(x => x.CharId == d.CharId, d, new ReplaceOptions { IsUpsert = true });
        }
        catch {   }
    }
}
