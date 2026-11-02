using RO2.Guilds.Domain;
using System.Collections.Concurrent;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using RO2.Persistence.Mongo.Persistence;

namespace RO2.Guilds.Infrastructure.Mongo;

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
