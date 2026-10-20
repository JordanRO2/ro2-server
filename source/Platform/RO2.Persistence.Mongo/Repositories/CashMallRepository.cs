using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Persistence.Mongo.Persistence;

namespace RO2.Persistence.Mongo.Repositories;

/// <summary>
/// MongoDB implementation of <see cref="ICashMallRepository"/> over the <c>cash_mall_accounts</c> collection (one
/// document per login account, <c>_id</c> = the account id) and the <c>cash_mall_counters</c> collection (the two
/// private id sequences). Stateless apart from its collection handles, so a DI singleton over the process-wide
/// <see cref="MongoDbContext"/>. tools/grant_mall_cash.py is a second, out-of-game writer to
/// <c>cash_mall_accounts</c>; it is not ported here.
/// </summary>
public sealed class CashMallRepository : ICashMallRepository
{
    private const string CollectionName = "cash_mall_accounts";
    private const string CountersCollectionName = "cash_mall_counters";

    /// <summary>Field of a counter document holding its last-issued sequence value (the document <c>_id</c> is the counter name).</summary>
    private const string CounterSequenceField = "seq";

    /// <summary>Positional-operator path to the <c>Count</c> of the <c>Entries</c> element matched by the filter's <c>$elemMatch</c>.</summary>
    private const string MatchedEntryCountPath = "Entries.$.Count";

    /// <summary>One account's cash mall: its balance (2868 f32 @2), its web-mall storage box (2638/1471/1472) and
    /// its purchase history (1594/2874). Id maps to Mongo <c>_id</c> = the account id.</summary>
    [BsonIgnoreExtraElements]
    private sealed class CashMallAccountDocument
    {
        public uint Id { get; set; }
        public long Balance { get; set; }
        public List<CashMallBoxEntry> Entries { get; set; } = new();
        public List<CashMallPurchase> Purchases { get; set; } = new();
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    private readonly IMongoCollection<CashMallAccountDocument> _docs;
    private readonly IMongoCollection<BsonDocument> _counters;

    public CashMallRepository(MongoDbContext context)
    {
        _docs = context.GetCollection<CashMallAccountDocument>(CollectionName);
        _counters = context.GetCollection<BsonDocument>(CountersCollectionName);
    }

    private async Task<CashMallAccountDocument?> GetAsync(uint accountId, CancellationToken ct = default)
        => await _docs.Find(d => d.Id == accountId).FirstOrDefaultAsync(ct);

    public async Task<long> GetBalanceAsync(uint accountId, CancellationToken ct = default)
        => (await GetAsync(accountId, ct))?.Balance ?? 0;

    public async Task<IReadOnlyList<CashMallBoxEntry>> GetBoxAsync(uint accountId, byte box, CancellationToken ct = default)
        => CashMallRules.LiveEntries((await GetAsync(accountId, ct))?.Entries, box);

    public async Task<CashMallBoxEntry?> GetBoxEntryAsync(uint accountId, uint entryId, CancellationToken ct = default)
        => (await GetAsync(accountId, ct))?.Entries.FirstOrDefault(e => e.EntryId == entryId && e.Count > 0);

    public async Task<(int Purchased, int Gifts)> GetBoxCountsAsync(uint accountId, CancellationToken ct = default)
        => CashMallRules.BoxCounts((await GetAsync(accountId, ct))?.Entries);

    public async Task<IReadOnlyList<CashMallPurchase>> GetPurchaseHistoryAsync(uint accountId, CancellationToken ct = default)
        => (await GetAsync(accountId, ct))?.Purchases ?? (IReadOnlyList<CashMallPurchase>)Array.Empty<CashMallPurchase>();

    public async Task<uint[]> ReserveIdsAsync(string counter, int count, CancellationToken ct = default)
    {
        if (count <= 0) return Array.Empty<uint>();
        var doc = await _counters.FindOneAndUpdateAsync(
            Builders<BsonDocument>.Filter.Eq("_id", counter),
            Builders<BsonDocument>.Update.Inc(CounterSequenceField, (long)count),
            new FindOneAndUpdateOptions<BsonDocument> { IsUpsert = true, ReturnDocument = ReturnDocument.After }, ct);
        long last = doc != null && doc.Contains(CounterSequenceField) ? doc[CounterSequenceField].ToInt64() : count;
        var ids = new uint[count];
        for (int i = 0; i < count; i++) ids[i] = (uint)(last - count + 1 + i);
        return ids;
    }

    public async Task<long?> TryPurchaseAsync(uint accountId, long total, IReadOnlyList<CashMallPurchase> log,
        IReadOnlyList<CashMallBoxEntry> ownEntries, CancellationToken ct = default)
    {
        var f = Builders<CashMallAccountDocument>.Filter;
        var u = Builders<CashMallAccountDocument>.Update;
        var update = u.Inc(d => d.Balance, -total).Set(d => d.UpdatedAt, DateTime.UtcNow);
        if (log.Count > 0) update = update.PushEach(d => d.Purchases, log);
        if (ownEntries.Count > 0) update = update.PushEach(d => d.Entries, ownEntries);
        var after = await _docs.FindOneAndUpdateAsync(f.Eq(d => d.Id, accountId) & f.Gte(d => d.Balance, total), update,
            new FindOneAndUpdateOptions<CashMallAccountDocument> { ReturnDocument = ReturnDocument.After }, ct);
        return after?.Balance;
    }

    public Task RefundAsync(uint accountId, long total, uint[] orderIds, uint[] entryIds, CancellationToken ct = default)
    {
        var update = Builders<CashMallAccountDocument>.Update
            .Inc(d => d.Balance, total)
            .Set(d => d.UpdatedAt, DateTime.UtcNow);
        if (orderIds.Length > 0) update = update.PullFilter(d => d.Purchases, p => orderIds.Contains(p.OrderId));
        if (entryIds.Length > 0) update = update.PullFilter(d => d.Entries, e => entryIds.Contains(e.EntryId));
        return _docs.UpdateOneAsync(d => d.Id == accountId, update, cancellationToken: ct);
    }

    public Task FileGiftAsync(uint accountId, IReadOnlyList<CashMallBoxEntry> entries, CancellationToken ct = default)
        => entries.Count == 0
            ? Task.CompletedTask
            : _docs.UpdateOneAsync(d => d.Id == accountId,
                Builders<CashMallAccountDocument>.Update.PushEach(d => d.Entries, entries).Set(d => d.UpdatedAt, DateTime.UtcNow),
                new UpdateOptions { IsUpsert = true }, ct);

    public async Task<CashMallBoxEntry?> TryTakeUnitsAsync(uint accountId, uint entryId, int units, CancellationToken ct = default)
    {
        if (units <= 0) return null;
        var f = Builders<CashMallAccountDocument>.Filter;
        var before = await _docs.FindOneAndUpdateAsync(
            f.Eq(d => d.Id, accountId) & f.ElemMatch(d => d.Entries, e => e.EntryId == entryId && e.Count >= units),
            Builders<CashMallAccountDocument>.Update.Inc(MatchedEntryCountPath, -units).Set(d => d.UpdatedAt, DateTime.UtcNow),
            new FindOneAndUpdateOptions<CashMallAccountDocument> { ReturnDocument = ReturnDocument.Before }, ct);
        var entry = before?.Entries.FirstOrDefault(e => e.EntryId == entryId);
        if (entry is null) return null;
        if (entry.Count == units)
            await _docs.UpdateOneAsync(d => d.Id == accountId,
                Builders<CashMallAccountDocument>.Update.PullFilter(d => d.Entries, e => e.EntryId == entryId && e.Count <= 0),
                cancellationToken: ct);
        return entry;
    }

    public async Task ReturnUnitsAsync(uint accountId, CashMallBoxEntry taken, int units, CancellationToken ct = default)
    {
        if (units <= 0) return;
        var f = Builders<CashMallAccountDocument>.Filter;
        var r = await _docs.UpdateOneAsync(
            f.Eq(d => d.Id, accountId) & f.ElemMatch(d => d.Entries, e => e.EntryId == taken.EntryId),
            Builders<CashMallAccountDocument>.Update.Inc(MatchedEntryCountPath, units), cancellationToken: ct);
        if (r.MatchedCount == 0)
            await FileGiftAsync(accountId, new[]
            {
                new CashMallBoxEntry
                {
                    EntryId = taken.EntryId, Box = taken.Box, ItemId = taken.ItemId, Count = units,
                    FiledUtc = taken.FiledUtc, SenderName = taken.SenderName,
                },
            }, ct);
    }
}
