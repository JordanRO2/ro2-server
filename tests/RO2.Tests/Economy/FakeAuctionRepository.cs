using RO2.Economy.Domain;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;

namespace RO2.Tests.Economy;

/// <summary>
/// In-memory <see cref="IAuctionRepository"/> for <see cref="EconomyFixture"/>. Every <c>Try*</c> is one atomic
/// find-and-update after a single scheduler point, with the Mongo repository's conditions: Active, the expiry side
/// it checks, and for the bid operations the bid the caller read (same top bidder and top bid). The returned
/// document is the one after the change; null when the condition no longer holds. Articles are copied in and out.
/// </summary>
public sealed class FakeAuctionRepository(RepoScheduler scheduler) : IAuctionRepository
{
    private readonly object _gate = new();
    private readonly Dictionary<ulong, AuctionArticle> _articles = [];
    private long _nextId;

    /// <summary>Copies of every stored article; takes no scheduler point.</summary>
    public IReadOnlyList<AuctionArticle> Articles { get { lock (_gate) return [.. _articles.Values.Select(EntityCopy.Clone)]; } }

    /// <summary>Test setup: stores a copy (Id and Uid assigned when 0) without a scheduler point; returns the copy.</summary>
    public AuctionArticle Seed(AuctionArticle article)
    {
        lock (_gate)
        {
            var row = EntityCopy.Clone(article);
            if (row.Id == 0) row.Id = (ulong)Interlocked.Increment(ref _nextId);
            else if (row.Id > (ulong)Interlocked.Read(ref _nextId)) Interlocked.Exchange(ref _nextId, (long)row.Id);
            if (row.Uid == 0) row.Uid = row.Id;
            _articles[row.Id] = row;
            return EntityCopy.Clone(row);
        }
    }

    public async Task<ulong> GetNextIdAsync(CancellationToken ct = default)
    {
        await scheduler.PointAsync("Auction.NextId");
        return (ulong)Interlocked.Increment(ref _nextId);
    }

    public async Task InsertAsync(AuctionArticle article, CancellationToken ct = default)
    {
        await scheduler.PointAsync("Auction.Insert");
        lock (_gate)
        {
            if (_articles.ContainsKey(article.Id)) throw new InvalidOperationException($"duplicate auction article id {article.Id}");
            _articles[article.Id] = EntityCopy.Clone(article);
        }
    }

    public async Task<AuctionArticle?> GetByUidAsync(ulong uid, CancellationToken ct = default)
    {
        await scheduler.PointAsync("Auction.GetByUid");
        lock (_gate) return _articles.Values.Where(a => a.Uid == uid).Select(EntityCopy.Clone).FirstOrDefault();
    }

    private static bool Live(AuctionArticle a, DateTime now) => a.State == AuctionArticleState.Active && a.ExpiresAt > now;

    public async Task<IReadOnlyList<AuctionArticle>> GetActiveBySellerAsync(uint sellerCharId, DateTime nowUtc, CancellationToken ct = default)
    {
        await scheduler.PointAsync("Auction.GetActiveBySeller");
        lock (_gate)
            return [.. _articles.Values.Where(a => a.SellerCharId == sellerCharId && Live(a, nowUtc))
                .OrderByDescending(a => a.StartedAt).Select(EntityCopy.Clone)];
    }

    public async Task<int> CountActiveBySellerAsync(uint sellerCharId, DateTime nowUtc, CancellationToken ct = default)
    {
        await scheduler.PointAsync("Auction.CountActiveBySeller");
        lock (_gate) return _articles.Values.Count(a => a.SellerCharId == sellerCharId && Live(a, nowUtc));
    }

    public async Task<IReadOnlyList<AuctionArticle>> GetActiveSortRowsAsync(IReadOnlyCollection<uint>? itemIds, DateTime nowUtc, CancellationToken ct = default)
    {
        await scheduler.PointAsync("Auction.GetActiveSortRows");
        if (itemIds is { Count: 0 }) return [];
        lock (_gate)
            return [.. _articles.Values.Where(a => Live(a, nowUtc) && (itemIds is null || itemIds.Contains(a.ItemId)))
                .Select(a => new AuctionArticle
                {
                    Id = a.Id, ItemId = a.ItemId, StartPrice = a.StartPrice, BuyoutPrice = a.BuyoutPrice,
                    TopBid = a.TopBid, TopBidderCharId = a.TopBidderCharId, ExpiresAt = a.ExpiresAt,
                })];
    }

    public async Task<IReadOnlyList<AuctionArticle>> GetByIdsAsync(IReadOnlyCollection<ulong> ids, CancellationToken ct = default)
    {
        await scheduler.PointAsync("Auction.GetByIds");
        lock (_gate) return [.. _articles.Values.Where(a => ids.Contains(a.Id)).Select(EntityCopy.Clone)];
    }

    public async Task<IReadOnlyList<uint>> GetActiveItemIdsAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        await scheduler.PointAsync("Auction.GetActiveItemIds");
        lock (_gate) return [.. _articles.Values.Where(a => Live(a, nowUtc)).Select(a => a.ItemId).Distinct()];
    }

    public async Task<IReadOnlyList<AuctionArticle>> GetDueBySellerAsync(uint sellerCharId, DateTime nowUtc, CancellationToken ct = default)
    {
        await scheduler.PointAsync("Auction.GetDueBySeller");
        lock (_gate)
            return [.. _articles.Values.Where(a => a.SellerCharId == sellerCharId
                                                   && a.State == AuctionArticleState.Active && a.ExpiresAt <= nowUtc)
                .Select(EntityCopy.Clone)];
    }

    public async Task<IReadOnlyList<AuctionArticle>> GetDueAsync(DateTime nowUtc, int limit, CancellationToken ct = default)
    {
        await scheduler.PointAsync("Auction.GetDue");
        lock (_gate)
            return [.. _articles.Values.Where(a => a.State == AuctionArticleState.Active && a.ExpiresAt <= nowUtc)
                .OrderBy(a => a.ExpiresAt).Take(Math.Max(1, limit)).Select(EntityCopy.Clone)];
    }

    // Active article of this uid satisfying `extra`: closed to `to` for `buyer` at `now`; the closed copy, or null.
    private AuctionArticle? Close(ulong uid, Func<AuctionArticle, bool> extra, AuctionArticleState to, uint buyer, DateTime now)
    {
        lock (_gate)
        {
            var a = _articles.Values.FirstOrDefault(x => x.Uid == uid && x.State == AuctionArticleState.Active && extra(x));
            if (a is null) return null;
            a.State = to;
            a.BuyerCharId = buyer;
            a.ClosedAt = now;
            return EntityCopy.Clone(a);
        }
    }

    public async Task<AuctionArticle?> TryCancelAsync(ulong uid, uint sellerCharId, DateTime nowUtc, CancellationToken ct = default)
    {
        await scheduler.PointAsync("Auction.TryCancel");
        return Close(uid, a => a.SellerCharId == sellerCharId, AuctionArticleState.Cancelled, 0, nowUtc);
    }

    public async Task<AuctionArticle?> TrySellAsync(ulong uid, uint buyerCharId, DateTime nowUtc, CancellationToken ct = default)
    {
        await scheduler.PointAsync("Auction.TrySell");
        return Close(uid, a => a.ExpiresAt > nowUtc, AuctionArticleState.Sold, buyerCharId, nowUtc);
    }

    public async Task<AuctionArticle?> TryExpireAsync(ulong uid, DateTime nowUtc, CancellationToken ct = default)
    {
        await scheduler.PointAsync("Auction.TryExpire");
        return Close(uid, a => a.ExpiresAt <= nowUtc, AuctionArticleState.Expired, 0, nowUtc);
    }

    // Active, unexpired, and still carrying the bid `read` carried (same top bidder and top bid; a no-bid read
    // matches any article with no top bidder). Called under _gate.
    private AuctionArticle? OpenWithBidOf(AuctionArticle read, DateTime now)
        => _articles.Values.FirstOrDefault(a => a.Uid == read.Uid && Live(a, now)
            && (read.TopBidderCharId == 0
                ? a.TopBidderCharId == 0
                : a.TopBidderCharId == read.TopBidderCharId && a.TopBid == read.TopBid));

    public async Task<AuctionArticle?> TryPlaceBidAsync(AuctionArticle read, uint bidderCharId, string bidderName,
        ulong bidderCharUid, long bid, DateTime nowUtc, CancellationToken ct = default)
    {
        await scheduler.PointAsync("Auction.TryPlaceBid");
        lock (_gate)
        {
            var a = OpenWithBidOf(read, nowUtc);
            if (a is null) return null;
            a.TopBid = bid;
            a.TopBidderCharId = bidderCharId;
            a.TopBidderName = bidderName;
            a.TopBidderCharUid = bidderCharUid;
            return EntityCopy.Clone(a);
        }
    }

    private AuctionArticle? SellToBidder(AuctionArticle read, uint sellerCharId, uint bidderCharId, string bidderName,
        ulong bidderCharUid, long bid, DateTime now)
    {
        lock (_gate)
        {
            var a = OpenWithBidOf(read, now);
            if (a is null || (sellerCharId != 0 && a.SellerCharId != sellerCharId)) return null;
            a.TopBid = bid;
            a.TopBidderCharId = bidderCharId;
            a.TopBidderName = bidderName;
            a.TopBidderCharUid = bidderCharUid;
            a.State = AuctionArticleState.Sold;
            a.BuyerCharId = bidderCharId;
            a.ClosedAt = now;
            return EntityCopy.Clone(a);
        }
    }

    public async Task<AuctionArticle?> TryWinAtInstantPriceAsync(AuctionArticle read, uint bidderCharId, string bidderName,
        ulong bidderCharUid, long bid, DateTime nowUtc, CancellationToken ct = default)
    {
        await scheduler.PointAsync("Auction.TryWinAtInstantPrice");
        return SellToBidder(read, 0, bidderCharId, bidderName, bidderCharUid, bid, nowUtc);
    }

    public async Task<AuctionArticle?> TryAcceptTopBidAsync(AuctionArticle read, uint sellerCharId, DateTime nowUtc, CancellationToken ct = default)
    {
        await scheduler.PointAsync("Auction.TryAcceptTopBid");
        return SellToBidder(read, sellerCharId, read.TopBidderCharId, read.TopBidderName, read.TopBidderCharUid,
            read.TopBid, nowUtc);
    }

    public async Task<IReadOnlyList<AuctionArticle>> GetTopBidsAsync(uint bidderCharId, DateTime nowUtc, CancellationToken ct = default)
    {
        await scheduler.PointAsync("Auction.GetTopBids");
        lock (_gate)
            return [.. _articles.Values.Where(a => a.TopBidderCharId == bidderCharId && Live(a, nowUtc))
                .OrderByDescending(a => a.StartedAt).Select(EntityCopy.Clone)];
    }

    public async Task<int> CountTopBidsAsync(uint bidderCharId, DateTime nowUtc, CancellationToken ct = default)
    {
        await scheduler.PointAsync("Auction.CountTopBids");
        lock (_gate) return _articles.Values.Count(a => a.TopBidderCharId == bidderCharId && Live(a, nowUtc));
    }
}
