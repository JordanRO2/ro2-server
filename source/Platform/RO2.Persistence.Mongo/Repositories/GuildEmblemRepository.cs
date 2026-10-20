using System.Collections.Concurrent;
using MongoDB.Driver;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Persistence.Mongo.Persistence;

namespace RO2.Persistence.Mongo.Repositories;

/// <summary>
/// MongoDB implementation of <see cref="IGuildEmblemRepository"/>, persisting to the <c>guild_emblems</c>
/// collection. A DI singleton over the process-wide <see cref="MongoDbContext"/>; the registry is loaded into
/// an in-memory map once on <see cref="InitAsync"/>, and before that the store is memory-only (so a unit test
/// can exercise it without a database).
/// </summary>
public sealed class GuildEmblemRepository : IGuildEmblemRepository
{
    private readonly ConcurrentDictionary<string, GuildEmblem> _byGuid = new();
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly Lock _gate = new();
    private readonly MongoDbContext _context;
    private IMongoCollection<GuildEmblem>? _collection;
    private volatile bool _loaded;

    public GuildEmblemRepository(MongoDbContext context) => _context = context;

 /// <summary>The document key for a 16-byte guild GUID: 32 lowercase hex chars.</summary>
    private static string KeyOf(ReadOnlySpan<byte> guid16) => Convert.ToHexStringLower(guid16);

    public async Task InitAsync()
    {
        if (_loaded) return;
        await _initLock.WaitAsync();
        try
        {
            if (_loaded) return;
            _collection = _context.GetCollection<GuildEmblem>("guild_emblems");
            foreach (var e in await _collection.Find(FilterDefinition<GuildEmblem>.Empty).ToListAsync())
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
        if (_collection != null)
            await _collection.ReplaceOneAsync(e => e.Id == key, emblem, new ReplaceOptions { IsUpsert = true });
        return emblem;
    }

    public async Task RemoveAsync(byte[] guid16)
    {
        var key = KeyOf(guid16);
        _byGuid.TryRemove(key, out _);
        try { if (_collection != null) await _collection.DeleteOneAsync(e => e.Id == key); }
        catch {   }
    }
}
