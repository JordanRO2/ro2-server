using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Persistence.Mongo.Persistence;

namespace RO2.Persistence.Mongo.Repositories;

/// <summary>
/// MongoDB implementation of <see cref="IGuildStorageLog"/> over two collections of the existing
/// <see cref="MongoDbContext"/>: <c>guild_storage_log</c> (the item history) and <c>guild_funds_log</c> (the
/// funds history). Stateless apart from the collection handles, so a DI singleton. Every method swallows
/// failures (history is best effort): a failed write is lost, a failed read is an empty page.
/// </summary>
public sealed class GuildStorageLogRepository : IGuildStorageLog
{
    private const string StorageLogCollectionName = "guild_storage_log";
    private const string FundsLogCollectionName = "guild_funds_log";

    /// <summary>Stored item line. <c>GuildKey</c> is the hex of the guild's 16-byte GUID.</summary>
    [BsonIgnoreExtraElements]
    private sealed class StorageDoc
    {
        public ObjectId Id { get; set; }
        public string GuildKey { get; set; } = string.Empty;
        public DateTime WhenUtc { get; set; }
        public string ActorName { get; set; } = string.Empty;
        public byte Kind { get; set; }
        public uint ItemId { get; set; }
        public int Count { get; set; }
    }

    /// <summary>Stored funds line, keyed like <see cref="StorageDoc"/>.</summary>
    [BsonIgnoreExtraElements]
    private sealed class FundsDoc
    {
        public ObjectId Id { get; set; }
        public string GuildKey { get; set; } = string.Empty;
        public DateTime WhenUtc { get; set; }
        public string ActorName { get; set; } = string.Empty;
        public byte Kind { get; set; }
        public long Amount { get; set; }
    }

    private readonly IMongoCollection<StorageDoc> _log;
    private readonly IMongoCollection<FundsDoc> _funds;

    public GuildStorageLogRepository(MongoDbContext context)
    {
        _log = context.GetCollection<StorageDoc>(StorageLogCollectionName);
        _funds = context.GetCollection<FundsDoc>(FundsLogCollectionName);
    }

    private static string KeyOf(byte[] guid16) => Convert.ToHexString(guid16);

    public async Task AddAsync(byte[] guid16, string actorName, byte kind, uint itemId, int count)
    {
        try
        {
            await _log.InsertOneAsync(new StorageDoc
            {
                GuildKey = KeyOf(guid16), WhenUtc = DateTime.UtcNow, ActorName = actorName ?? string.Empty,
                Kind = kind, ItemId = itemId, Count = count,
            });
        }
        catch { /* history is best effort */ }
    }

    public async Task<(long Total, IReadOnlyList<GuildStorageLogEntry> Lines)> GetPageAsync(byte[] guid16, int page, int days, int pageSize)
    {
        try
        {
            var f = Builders<StorageDoc>.Filter;
            var filter = f.Eq(e => e.GuildKey, KeyOf(guid16));
            if (days > 0) filter &= f.Gte(e => e.WhenUtc, DateTime.UtcNow.AddDays(-days));
            var total = await _log.CountDocumentsAsync(filter);
            var size = Math.Max(1, pageSize);
            var docs = await _log.Find(filter).SortByDescending(e => e.WhenUtc)
                .Skip(Math.Max(0, page) * size).Limit(size).ToListAsync();
            var lines = docs.Select(d => new GuildStorageLogEntry
            {
                WhenUtc = d.WhenUtc, ActorName = d.ActorName, Kind = d.Kind, ItemId = d.ItemId, Count = d.Count,
            }).ToList();
            return (total, lines);
        }
        catch
        {
            return (0, Array.Empty<GuildStorageLogEntry>());
        }
    }

    public async Task AddFundsAsync(byte[] guid16, string actorName, byte kind, long amount)
    {
        try
        {
            await _funds.InsertOneAsync(new FundsDoc
            {
                GuildKey = KeyOf(guid16), WhenUtc = DateTime.UtcNow, ActorName = actorName ?? string.Empty,
                Kind = kind, Amount = amount,
            });
        }
        catch { /* history is best effort */ }
    }

    public async Task<(long Total, IReadOnlyList<GuildFundsLogEntry> Lines)> GetFundsPageAsync(byte[] guid16, int page, int days, int pageSize)
    {
        try
        {
            var f = Builders<FundsDoc>.Filter;
            var filter = f.Eq(e => e.GuildKey, KeyOf(guid16));
            if (days > 0) filter &= f.Gte(e => e.WhenUtc, DateTime.UtcNow.AddDays(-days));
            var total = await _funds.CountDocumentsAsync(filter);
            var size = Math.Max(1, pageSize);
            var docs = await _funds.Find(filter).SortByDescending(e => e.WhenUtc)
                .Skip(Math.Max(0, page) * size).Limit(size).ToListAsync();
            var lines = docs.Select(d => new GuildFundsLogEntry
            {
                WhenUtc = d.WhenUtc, ActorName = d.ActorName, Kind = d.Kind, Amount = d.Amount,
            }).ToList();
            return (total, lines);
        }
        catch
        {
            return (0, Array.Empty<GuildFundsLogEntry>());
        }
    }
}
