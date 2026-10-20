using MongoDB.Bson;
using MongoDB.Driver;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Persistence.Mongo.Persistence;

namespace RO2.Persistence.Mongo.Repositories;

/// <summary>
/// MongoDB implementation of <see cref="IAuctionRepository"/>: owns <c>auction_articles</c> + <c>auction_counters</c>
/// over the process-wide <see cref="MongoDbContext"/> singleton. Every state change is a single conditional
/// FindOneAndUpdate on <c>State == Active</c>, so a double click, or two buyers racing for one article, can close
/// it once only. Registered transient by the Zone host: the constructor builds the indexes once per process and
/// retries on the next construction if that failed (kept in the ctor for Phase 1).
/// </summary>
public sealed class AuctionRepository : IAuctionRepository
{
    private const string ArticlesCollection = "auction_articles";
    private const string CountersCollection = "auction_counters";

    /// <summary>The <c>_id</c> of the counter document that hands out article ids.</summary>
    private const string CounterId = "auctionId";

    /// <summary>The counter document's field holding the last article id handed out.</summary>
    private const string CounterSequenceField = "seq";

    /// <summary>The first article id, returned when the counter document has no sequence yet.</summary>
    private const long FirstArticleId = 1L;

    private readonly IMongoCollection<AuctionArticle> _articles;
    private readonly IMongoCollection<BsonDocument> _counters;

    private static int _indexesEnsured; // 0 = not yet

    public AuctionRepository(MongoDbContext context)
    {
        _articles = context.GetCollection<AuctionArticle>(ArticlesCollection);
        _counters = context.GetCollection<BsonDocument>(CountersCollection);
        EnsureIndexes();
    }

    private void EnsureIndexes()
    {
        if (Interlocked.Exchange(ref _indexesEnsured, 1) != 0) return;
        try
        {
            _articles.Indexes.CreateMany(new[]
            {
                new CreateIndexModel<AuctionArticle>(
                    Builders<AuctionArticle>.IndexKeys.Ascending(a => a.SellerCharId).Ascending(a => a.State)),
                new CreateIndexModel<AuctionArticle>(
                    Builders<AuctionArticle>.IndexKeys.Ascending(a => a.Uid),
                    new CreateIndexOptions { Unique = true }),
                new CreateIndexModel<AuctionArticle>( // the public search (GetActiveSortRowsAsync)
                    Builders<AuctionArticle>.IndexKeys.Ascending(a => a.State).Ascending(a => a.ItemId)),
                new CreateIndexModel<AuctionArticle>( // the expiry sweep (GetDueAsync)
                    Builders<AuctionArticle>.IndexKeys.Ascending(a => a.State).Ascending(a => a.ExpiresAt)),
                new CreateIndexModel<AuctionArticle>( // a bidder's top bids (2457 list, the 100-bid cap)
                    Builders<AuctionArticle>.IndexKeys.Ascending(a => a.TopBidderCharId).Ascending(a => a.State)),
            });
        }
        catch
        {
            // A racing/duplicate index build is harmless; never let index setup take the server down.
            Interlocked.Exchange(ref _indexesEnsured, 0);
        }
    }

    public async Task<ulong> GetNextIdAsync(CancellationToken ct = default)
    {
        var filter = Builders<BsonDocument>.Filter.Eq("_id", CounterId);
        var update = Builders<BsonDocument>.Update.Inc(CounterSequenceField, 1L);
        var opts = new FindOneAndUpdateOptions<BsonDocument> { IsUpsert = true, ReturnDocument = ReturnDocument.After };
        var doc = await _counters.FindOneAndUpdateAsync(filter, update, opts, ct);
        return (ulong)(doc != null && doc.Contains(CounterSequenceField) ? doc[CounterSequenceField].ToInt64() : FirstArticleId);
    }

    public Task InsertAsync(AuctionArticle article, CancellationToken ct = default)
        => _articles.InsertOneAsync(article, cancellationToken: ct);

    public async Task<AuctionArticle?> GetByUidAsync(ulong uid, CancellationToken ct = default)
    {
        return await _articles.Find(a => a.Uid == uid).FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<AuctionArticle>> GetActiveBySellerAsync(uint sellerCharId, DateTime nowUtc, CancellationToken ct = default)
    {
        return await _articles.Find(a => a.SellerCharId == sellerCharId && a.State == AuctionArticleState.Active && a.ExpiresAt > nowUtc)
            .SortByDescending(a => a.StartedAt)
            .ToListAsync(ct);
    }

    public async Task<int> CountActiveBySellerAsync(uint sellerCharId, DateTime nowUtc, CancellationToken ct = default) =>
        (int)Math.Min(int.MaxValue, await _articles.CountDocumentsAsync(
            a => a.SellerCharId == sellerCharId && a.State == AuctionArticleState.Active && a.ExpiresAt > nowUtc,
            cancellationToken: ct));

    public async Task<IReadOnlyList<AuctionArticle>> GetActiveSortRowsAsync(IReadOnlyCollection<uint>? itemIds, DateTime nowUtc, CancellationToken ct = default)
    {
        if (itemIds is { Count: 0 }) return new List<AuctionArticle>();

        var f = Builders<AuctionArticle>.Filter;
        var filter = f.Eq(a => a.State, AuctionArticleState.Active) & f.Gt(a => a.ExpiresAt, nowUtc);
        if (itemIds != null) filter &= f.In(a => a.ItemId, itemIds);

        // Only the sort inputs (and _id), read into AuctionArticle itself: a field the document lacks - the bid
        // fields of an article stored before they existed - then loads with the class default 0, as on every
        // full read, and the caller's CurrentPrice falls back to the start price.
        var sortFields = Builders<AuctionArticle>.Projection
            .Include(a => a.ItemId).Include(a => a.StartPrice).Include(a => a.BuyoutPrice)
            .Include(a => a.TopBid).Include(a => a.TopBidderCharId).Include(a => a.ExpiresAt);
        return await _articles.Find(filter).Project<AuctionArticle>(sortFields).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<AuctionArticle>> GetByIdsAsync(IReadOnlyCollection<ulong> ids, CancellationToken ct = default)
    {
        return await _articles.Find(Builders<AuctionArticle>.Filter.In(a => a.Id, ids)).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<uint>> GetActiveItemIdsAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        var cursor = await _articles.DistinctAsync(a => a.ItemId,
            a => a.State == AuctionArticleState.Active && a.ExpiresAt > nowUtc, cancellationToken: ct);
        return await cursor.ToListAsync(ct);
    }

    public async Task<IReadOnlyList<AuctionArticle>> GetDueBySellerAsync(uint sellerCharId, DateTime nowUtc, CancellationToken ct = default)
    {
        return await _articles.Find(a => a.SellerCharId == sellerCharId && a.State == AuctionArticleState.Active && a.ExpiresAt <= nowUtc)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<AuctionArticle>> GetDueAsync(DateTime nowUtc, int limit, CancellationToken ct = default)
    {
        return await _articles.Find(a => a.State == AuctionArticleState.Active && a.ExpiresAt <= nowUtc)
            .SortBy(a => a.ExpiresAt)
            .Limit(Math.Max(1, limit))
            .ToListAsync(ct);
    }

    public Task<AuctionArticle?> TryCancelAsync(ulong uid, uint sellerCharId, DateTime nowUtc, CancellationToken ct = default) =>
        CloseAsync(Builders<AuctionArticle>.Filter.Eq(a => a.SellerCharId, sellerCharId),
            uid, AuctionArticleState.Cancelled, 0, nowUtc, ct);

    public Task<AuctionArticle?> TrySellAsync(ulong uid, uint buyerCharId, DateTime nowUtc, CancellationToken ct = default) =>
        CloseAsync(Builders<AuctionArticle>.Filter.Gt(a => a.ExpiresAt, nowUtc),
            uid, AuctionArticleState.Sold, buyerCharId, nowUtc, ct);

    public Task<AuctionArticle?> TryExpireAsync(ulong uid, DateTime nowUtc, CancellationToken ct = default) =>
        CloseAsync(Builders<AuctionArticle>.Filter.Lte(a => a.ExpiresAt, nowUtc),
            uid, AuctionArticleState.Expired, 0, nowUtc, ct);

    private async Task<AuctionArticle?> CloseAsync(FilterDefinition<AuctionArticle> extra, ulong uid,
        AuctionArticleState to, uint buyerCharId, DateTime nowUtc, CancellationToken ct)
    {
        var filter = Builders<AuctionArticle>.Filter.Eq(a => a.Uid, uid)
                     & Builders<AuctionArticle>.Filter.Eq(a => a.State, AuctionArticleState.Active)
                     & extra;
        var update = Builders<AuctionArticle>.Update
            .Set(a => a.State, to)
            .Set(a => a.BuyerCharId, buyerCharId)
            .Set(a => a.ClosedAt, (DateTime?)nowUtc);
        return await _articles.FindOneAndUpdateAsync(filter, update,
            new FindOneAndUpdateOptions<AuctionArticle> { ReturnDocument = ReturnDocument.After }, ct);
    }

    // ---- bid operations (1407 / 1404) ----

    /// <summary>
    /// The article still carries the bid <paramref name="read"/> carried: same top bidder and top bid. A
    /// no-bid read also matches a document stored before the bid fields existed, where the field is missing
    /// and an equality on 0 would not match.
    /// </summary>
    private static FilterDefinition<AuctionArticle> BidUnchanged(AuctionArticle read)
    {
        var f = Builders<AuctionArticle>.Filter;
        if (read.TopBidderCharId == 0)
            return f.Or(f.Eq(a => a.TopBidderCharId, 0u), f.Exists(a => a.TopBidderCharId, false));
        return f.Eq(a => a.TopBidderCharId, read.TopBidderCharId) & f.Eq(a => a.TopBid, read.TopBid);
    }

    /// <summary>Active, unexpired and still with the bid of <paramref name="read"/>.</summary>
    private static FilterDefinition<AuctionArticle> OpenWithBidOf(AuctionArticle read, DateTime nowUtc)
    {
        var f = Builders<AuctionArticle>.Filter;
        return f.Eq(a => a.Uid, read.Uid)
               & f.Eq(a => a.State, AuctionArticleState.Active)
               & f.Gt(a => a.ExpiresAt, nowUtc)
               & BidUnchanged(read);
    }

    public async Task<AuctionArticle?> TryPlaceBidAsync(AuctionArticle read, uint bidderCharId, string bidderName,
        ulong bidderCharUid, long bid, DateTime nowUtc, CancellationToken ct = default)
    {
        var update = Builders<AuctionArticle>.Update
            .Set(a => a.TopBid, bid)
            .Set(a => a.TopBidderCharId, bidderCharId)
            .Set(a => a.TopBidderName, bidderName)
            .Set(a => a.TopBidderCharUid, bidderCharUid);
        return await _articles.FindOneAndUpdateAsync(OpenWithBidOf(read, nowUtc), update,
            new FindOneAndUpdateOptions<AuctionArticle> { ReturnDocument = ReturnDocument.After }, ct);
    }

    /// <summary>
    /// Active -> Sold to <paramref name="bidderCharId"/> at <paramref name="bid"/>, recorded as the top bid,
    /// under the same condition as <see cref="TryPlaceBidAsync"/>, plus the article's seller being
    /// <paramref name="sellerCharId"/> when that is not 0. Null when that no longer holds.
    /// </summary>
    private async Task<AuctionArticle?> TrySellToBidderAsync(AuctionArticle read, uint sellerCharId, uint bidderCharId,
        string bidderName, ulong bidderCharUid, long bid, DateTime nowUtc, CancellationToken ct)
    {
        var filter = OpenWithBidOf(read, nowUtc);
        if (sellerCharId != 0) filter &= Builders<AuctionArticle>.Filter.Eq(a => a.SellerCharId, sellerCharId);
        var update = Builders<AuctionArticle>.Update
            .Set(a => a.TopBid, bid)
            .Set(a => a.TopBidderCharId, bidderCharId)
            .Set(a => a.TopBidderName, bidderName)
            .Set(a => a.TopBidderCharUid, bidderCharUid)
            .Set(a => a.State, AuctionArticleState.Sold)
            .Set(a => a.BuyerCharId, bidderCharId)
            .Set(a => a.ClosedAt, (DateTime?)nowUtc);
        return await _articles.FindOneAndUpdateAsync(filter, update,
            new FindOneAndUpdateOptions<AuctionArticle> { ReturnDocument = ReturnDocument.After }, ct);
    }

    public Task<AuctionArticle?> TryWinAtInstantPriceAsync(AuctionArticle read, uint bidderCharId, string bidderName,
        ulong bidderCharUid, long bid, DateTime nowUtc, CancellationToken ct = default) =>
        TrySellToBidderAsync(read, 0, bidderCharId, bidderName, bidderCharUid, bid, nowUtc, ct);

    public Task<AuctionArticle?> TryAcceptTopBidAsync(AuctionArticle read, uint sellerCharId, DateTime nowUtc, CancellationToken ct = default) =>
        TrySellToBidderAsync(read, sellerCharId, read.TopBidderCharId, read.TopBidderName, read.TopBidderCharUid,
            read.TopBid, nowUtc, ct);

    public async Task<IReadOnlyList<AuctionArticle>> GetTopBidsAsync(uint bidderCharId, DateTime nowUtc, CancellationToken ct = default)
    {
        return await _articles.Find(a => a.TopBidderCharId == bidderCharId && a.State == AuctionArticleState.Active && a.ExpiresAt > nowUtc)
            .SortByDescending(a => a.StartedAt)
            .ToListAsync(ct);
    }

    public async Task<int> CountTopBidsAsync(uint bidderCharId, DateTime nowUtc, CancellationToken ct = default) =>
        (int)Math.Min(int.MaxValue, await _articles.CountDocumentsAsync(
            a => a.TopBidderCharId == bidderCharId && a.State == AuctionArticleState.Active && a.ExpiresAt > nowUtc,
            cancellationToken: ct));
}
