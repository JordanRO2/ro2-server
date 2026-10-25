using RO2.Economy.Infrastructure.Postgres;
using RO2.Economy.Domain;
using RO2.Domain.Entities;
using RO2.Persistence.Postgres.Repositories;
using Xunit;

namespace RO2.Tests.Integration;

/// <summary>
/// Runtime tests for <see cref="AuctionRepository"/> against a live PostgreSQL instance
/// (<see cref="PostgresFixture"/>). Covers the id counter, inserts and round-trips (columns and jsonb
/// snapshot), active, due and top-bid queries, the search projection, and each compare-and-set path (cancel,
/// sell, expire, place bid, win at instant, accept top bid) including success, no-match, wrong-key,
/// concurrent-change and expiry cases.
/// </summary>
/// <remarks>Each test uses fresh unique keys so tests sharing one container never collide.</remarks>
[Collection("Postgres")]
public sealed class PostgresAuctionTests
{
    private readonly PostgresFixture _fx;
    public PostgresAuctionTests(PostgresFixture fx) => _fx = fx;

    private static long _keys = DateTime.UtcNow.Ticks;
    private static ulong Key() => (ulong)Interlocked.Increment(ref _keys);

    private static AuctionArticle NewActive(uint seller, uint item, long start, long buyout,
        DateTime startedAt, DateTime expiresAt)
    {
        var uid = Key();
        return new AuctionArticle
        {
            Id = Key(),
            Uid = uid,
            SellerCharId = seller,
            SellerName = "S" + seller,
            SellerCharUid = Key(),
            ItemUid = Key(),
            ItemId = item,
            Count = 3,
            Refine = 5,
            Durability = 40,
            CardsJson = "[9]",
            Element = 2,
            DyeColor = 123456,
            SocketPunchersJson = null,
            DyeColor2 = 7,
            DyeColor3 = 8,
            FxEnchantIndex = 4,
            EnchantReinforceId = 6,
            EnchantSeconds = 10,
            Bound = true,
            AbilityValue = 55,
            StartPrice = start,
            BuyoutPrice = buyout,
            StartedAt = startedAt,
            ExpiresAt = expiresAt,
            State = AuctionArticleState.Active,
        };
    }

    [Fact]
    public async Task GetNextId_is_monotonic()
    {
        if (!_fx.Available) return;
        var repo = new AuctionRepository(_fx.Db!);
        var a = await repo.GetNextIdAsync();
        var b = await repo.GetNextIdAsync();
        Assert.Equal(a + 1, b);
    }

    [Fact]
    public async Task Insert_then_GetByUid_round_trips_all_fields()
    {
        if (!_fx.Available) return;
        var repo = new AuctionRepository(_fx.Db!);
        var now = DateTime.UtcNow;
        var a = NewActive((uint)Key(), (uint)Key(), 100, 500, now, now.AddHours(1));
        a.TopBid = 0;
        await repo.InsertAsync(a);

        var got = await repo.GetByUidAsync(a.Uid);
        Assert.NotNull(got);
        Assert.Equal(a.Id, got!.Id);
        Assert.Equal(a.Uid, got.Uid);
        Assert.Equal(a.SellerCharId, got.SellerCharId);
        Assert.Equal(a.SellerName, got.SellerName);
        Assert.Equal(a.SellerCharUid, got.SellerCharUid);
        Assert.Equal(a.ItemUid, got.ItemUid);
        Assert.Equal(a.ItemId, got.ItemId);
        Assert.Equal(a.Count, got.Count);
        Assert.Equal(a.Refine, got.Refine);
        Assert.Equal(a.Durability, got.Durability);
        Assert.Equal(a.CardsJson, got.CardsJson);
        Assert.Equal(a.Element, got.Element);
        Assert.Equal(a.DyeColor, got.DyeColor);
        Assert.Null(got.SocketPunchersJson);
        Assert.Equal(a.DyeColor2, got.DyeColor2);
        Assert.Equal(a.DyeColor3, got.DyeColor3);
        Assert.Equal(a.FxEnchantIndex, got.FxEnchantIndex);
        Assert.Equal(a.EnchantReinforceId, got.EnchantReinforceId);
        Assert.Equal(a.EnchantSeconds, got.EnchantSeconds);
        Assert.True(got.Bound);
        Assert.Equal(a.AbilityValue, got.AbilityValue);
        Assert.Equal(a.StartPrice, got.StartPrice);
        Assert.Equal(a.BuyoutPrice, got.BuyoutPrice);
        Assert.Equal(AuctionArticleState.Active, got.State);
        Assert.Equal(0u, got.BuyerCharId);
        Assert.Null(got.ClosedAt);
        Assert.Equal(a.StartedAt, got.StartedAt, TimeSpan.FromMilliseconds(1));
        Assert.Equal(a.ExpiresAt, got.ExpiresAt, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task GetByUid_unknown_is_null()
    {
        if (!_fx.Available) return;
        var repo = new AuctionRepository(_fx.Db!);
        Assert.Null(await repo.GetByUidAsync(Key()));
    }

    [Fact]
    public async Task GetActiveBySeller_and_count_newest_first_excludes_expired_and_closed()
    {
        if (!_fx.Available) return;
        var repo = new AuctionRepository(_fx.Db!);
        var now = DateTime.UtcNow;
        var seller = (uint)Key();
        var older = NewActive(seller, (uint)Key(), 1, 0, now.AddMinutes(-10), now.AddHours(1));
        var newer = NewActive(seller, (uint)Key(), 1, 0, now.AddMinutes(-1), now.AddHours(1));
        var expired = NewActive(seller, (uint)Key(), 1, 0, now.AddMinutes(-5), now.AddHours(-1));
        await repo.InsertAsync(older);
        await repo.InsertAsync(newer);
        await repo.InsertAsync(expired);

        var live = await repo.GetActiveBySellerAsync(seller, now);
        Assert.Equal(2, live.Count);
        Assert.Equal(newer.Uid, live[0].Uid); 
        Assert.Equal(older.Uid, live[1].Uid);
        Assert.Equal(2, await repo.CountActiveBySellerAsync(seller, now));
    }

    [Fact]
    public async Task GetActiveSortRows_projects_only_sort_inputs_and_filters_items()
    {
        if (!_fx.Available) return;
        var repo = new AuctionRepository(_fx.Db!);
        var now = DateTime.UtcNow;
        var item = (uint)Key();
        var a = NewActive((uint)Key(), item, 100, 500, now, now.AddHours(1));
        await repo.InsertAsync(a);

        var rows = await repo.GetActiveSortRowsAsync(new[] { item }, now);
        var row = Assert.Single(rows);
        Assert.Equal(a.Id, row.Id);
        Assert.Equal(item, row.ItemId);
        Assert.Equal(100, row.StartPrice);
        Assert.Equal(500, row.BuyoutPrice);
        Assert.Equal(0, row.TopBid);
        Assert.Equal(0u, row.TopBidderCharId);
        Assert.Equal(a.ExpiresAt, row.ExpiresAt, TimeSpan.FromMilliseconds(1));
        
        Assert.Equal(string.Empty, row.SellerName);
        Assert.Equal((short)-1, row.Durability);
        Assert.Equal(0, row.Count);

        
        var all = await repo.GetActiveSortRowsAsync(null, now);
        var mine = Assert.Single(all, x => x.Id == a.Id);
        Assert.Equal(string.Empty, mine.SellerName);

        
        Assert.Empty(await repo.GetActiveSortRowsAsync(Array.Empty<uint>(), now));
    }

    [Fact]
    public async Task GetByIds_returns_present_skips_absent_and_empty()
    {
        if (!_fx.Available) return;
        var repo = new AuctionRepository(_fx.Db!);
        var now = DateTime.UtcNow;
        var a = NewActive((uint)Key(), (uint)Key(), 1, 0, now, now.AddHours(1));
        var b = NewActive((uint)Key(), (uint)Key(), 1, 0, now, now.AddHours(1));
        await repo.InsertAsync(a);
        await repo.InsertAsync(b);

        var got = await repo.GetByIdsAsync(new[] { a.Id, b.Id, Key() });
        Assert.Equal(2, got.Count);
        Assert.Contains(got, x => x.Id == a.Id);
        Assert.Contains(got, x => x.Id == b.Id);

        Assert.Empty(await repo.GetByIdsAsync(Array.Empty<ulong>()));
    }

    [Fact]
    public async Task GetActiveItemIds_is_distinct_and_excludes_expired()
    {
        if (!_fx.Available) return;
        var repo = new AuctionRepository(_fx.Db!);
        var now = DateTime.UtcNow;
        var liveItem = (uint)Key();
        var deadItem = (uint)Key();
        await repo.InsertAsync(NewActive((uint)Key(), liveItem, 1, 0, now, now.AddHours(1)));
        await repo.InsertAsync(NewActive((uint)Key(), liveItem, 1, 0, now, now.AddHours(1)));
        await repo.InsertAsync(NewActive((uint)Key(), deadItem, 1, 0, now, now.AddHours(-1)));

        var ids = await repo.GetActiveItemIdsAsync(now);
        Assert.Single(ids, x => x == liveItem);
        Assert.DoesNotContain(deadItem, ids);
    }

    [Fact]
    public async Task GetDueBySeller_returns_only_expired_active()
    {
        if (!_fx.Available) return;
        var repo = new AuctionRepository(_fx.Db!);
        var now = DateTime.UtcNow;
        var seller = (uint)Key();
        var due = NewActive(seller, (uint)Key(), 1, 0, now.AddHours(-2), now.AddHours(-1));
        var live = NewActive(seller, (uint)Key(), 1, 0, now, now.AddHours(1));
        await repo.InsertAsync(due);
        await repo.InsertAsync(live);

        var got = await repo.GetDueBySellerAsync(seller, now);
        var only = Assert.Single(got);
        Assert.Equal(due.Uid, only.Uid);
    }

    [Fact]
    public async Task GetDue_earliest_first_and_limited()
    {
        if (!_fx.Available) return;
        var repo = new AuctionRepository(_fx.Db!);
        
        var baseT = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var a0 = NewActive((uint)Key(), (uint)Key(), 1, 0, baseT.AddDays(-1), baseT);
        var a1 = NewActive((uint)Key(), (uint)Key(), 1, 0, baseT.AddDays(-1), baseT.AddSeconds(1));
        var a2 = NewActive((uint)Key(), (uint)Key(), 1, 0, baseT.AddDays(-1), baseT.AddSeconds(2));
        await repo.InsertAsync(a2);
        await repo.InsertAsync(a0);
        await repo.InsertAsync(a1);

        var two = await repo.GetDueAsync(DateTime.UtcNow, 2);
        Assert.Equal(2, two.Count);
        Assert.Equal(a0.Uid, two[0].Uid);
        Assert.Equal(a1.Uid, two[1].Uid);

        var one = await repo.GetDueAsync(DateTime.UtcNow, 0); 
        Assert.Single(one);
        Assert.Equal(a0.Uid, one[0].Uid);
    }

    [Fact]
    public async Task TryCancel_success_wrong_seller_and_double()
    {
        if (!_fx.Available) return;
        var repo = new AuctionRepository(_fx.Db!);
        var now = DateTime.UtcNow;
        var seller = (uint)Key();
        var a = NewActive(seller, (uint)Key(), 1, 0, now, now.AddHours(1));
        await repo.InsertAsync(a);

        Assert.Null(await repo.TryCancelAsync(a.Uid, seller + 1, now)); 
        var ok = await repo.TryCancelAsync(a.Uid, seller, now);
        Assert.NotNull(ok);
        Assert.Equal(AuctionArticleState.Cancelled, ok!.State);
        Assert.Equal(0u, ok.BuyerCharId);
        Assert.NotNull(ok.ClosedAt);
        Assert.Null(await repo.TryCancelAsync(a.Uid, seller, now)); 
    }

    [Fact]
    public async Task TrySell_success_expired_and_already_closed()
    {
        if (!_fx.Available) return;
        var repo = new AuctionRepository(_fx.Db!);
        var now = DateTime.UtcNow;
        var live = NewActive((uint)Key(), (uint)Key(), 1, 0, now, now.AddHours(1));
        await repo.InsertAsync(live);
        var buyer = (uint)Key();
        var sold = await repo.TrySellAsync(live.Uid, buyer, now);
        Assert.NotNull(sold);
        Assert.Equal(AuctionArticleState.Sold, sold!.State);
        Assert.Equal(buyer, sold.BuyerCharId);
        Assert.NotNull(sold.ClosedAt);
        Assert.Null(await repo.TrySellAsync(live.Uid, buyer, now)); 

        var expired = NewActive((uint)Key(), (uint)Key(), 1, 0, now.AddHours(-2), now.AddHours(-1));
        await repo.InsertAsync(expired);
        Assert.Null(await repo.TrySellAsync(expired.Uid, buyer, now)); 
    }

    [Fact]
    public async Task TryExpire_only_when_past_expiry()
    {
        if (!_fx.Available) return;
        var repo = new AuctionRepository(_fx.Db!);
        var now = DateTime.UtcNow;
        var notDue = NewActive((uint)Key(), (uint)Key(), 1, 0, now, now.AddHours(1));
        await repo.InsertAsync(notDue);
        Assert.Null(await repo.TryExpireAsync(notDue.Uid, now)); 

        var due = NewActive((uint)Key(), (uint)Key(), 1, 0, now.AddHours(-2), now.AddHours(-1));
        await repo.InsertAsync(due);
        var exp = await repo.TryExpireAsync(due.Uid, now);
        Assert.NotNull(exp);
        Assert.Equal(AuctionArticleState.Expired, exp!.State);
        Assert.Equal(0u, exp.BuyerCharId);
        Assert.NotNull(exp.ClosedAt);
    }

    [Fact]
    public async Task TryPlaceBid_first_bid_stale_read_raise_and_expired()
    {
        if (!_fx.Available) return;
        var repo = new AuctionRepository(_fx.Db!);
        var now = DateTime.UtcNow;
        var a = NewActive((uint)Key(), (uint)Key(), 10, 0, now, now.AddHours(1));
        await repo.InsertAsync(a);

        var read0 = await repo.GetByUidAsync(a.Uid);
        Assert.NotNull(read0);
        var bidderA = (uint)Key();
        var first = await repo.TryPlaceBidAsync(read0!, bidderA, "A", Key(), 200, now);
        Assert.NotNull(first);
        Assert.Equal(200, first!.TopBid);
        Assert.Equal(bidderA, first.TopBidderCharId);
        Assert.Equal("A", first.TopBidderName);
        Assert.Equal(AuctionArticleState.Active, first.State);

        
        var stale = await repo.TryPlaceBidAsync(read0!, (uint)Key(), "B", Key(), 300, now);
        Assert.Null(stale);

        
        var read1 = await repo.GetByUidAsync(a.Uid);
        var bidderB = (uint)Key();
        var second = await repo.TryPlaceBidAsync(read1!, bidderB, "B", Key(), 300, now);
        Assert.NotNull(second);
        Assert.Equal(300, second!.TopBid);
        Assert.Equal(bidderB, second.TopBidderCharId);

        
        var expired = NewActive((uint)Key(), (uint)Key(), 10, 0, now.AddHours(-2), now.AddHours(-1));
        await repo.InsertAsync(expired);
        var readExp = await repo.GetByUidAsync(expired.Uid);
        Assert.Null(await repo.TryPlaceBidAsync(readExp!, (uint)Key(), "C", Key(), 50, now));
    }

    [Fact]
    public async Task TryWinAtInstantPrice_success_and_stale()
    {
        if (!_fx.Available) return;
        var repo = new AuctionRepository(_fx.Db!);
        var now = DateTime.UtcNow;
        var a = NewActive((uint)Key(), (uint)Key(), 10, 1000, now, now.AddHours(1));
        await repo.InsertAsync(a);

        var read0 = await repo.GetByUidAsync(a.Uid);
        var winner = (uint)Key();
        var won = await repo.TryWinAtInstantPriceAsync(read0!, winner, "W", Key(), 1000, now);
        Assert.NotNull(won);
        Assert.Equal(AuctionArticleState.Sold, won!.State);
        Assert.Equal(winner, won.BuyerCharId);
        Assert.Equal(winner, won.TopBidderCharId);
        Assert.Equal(1000, won.TopBid);

        
        Assert.Null(await repo.TryWinAtInstantPriceAsync(read0!, (uint)Key(), "X", Key(), 1000, now));
    }

    [Fact]
    public async Task TryAcceptTopBid_success_wrong_seller_and_stale()
    {
        if (!_fx.Available) return;
        var repo = new AuctionRepository(_fx.Db!);
        var now = DateTime.UtcNow;
        var seller = (uint)Key();

        
        var a = NewActive(seller, (uint)Key(), 10, 0, now, now.AddHours(1));
        await repo.InsertAsync(a);
        var bidderA = (uint)Key();
        await repo.TryPlaceBidAsync((await repo.GetByUidAsync(a.Uid))!, bidderA, "A", Key(), 200, now);
        var readWithBid = await repo.GetByUidAsync(a.Uid);
        var accepted = await repo.TryAcceptTopBidAsync(readWithBid!, seller, now);
        Assert.NotNull(accepted);
        Assert.Equal(AuctionArticleState.Sold, accepted!.State);
        Assert.Equal(bidderA, accepted.BuyerCharId);
        Assert.Equal(bidderA, accepted.TopBidderCharId);
        Assert.NotNull(accepted.ClosedAt);

        
        var b = NewActive(seller, (uint)Key(), 10, 0, now, now.AddHours(1));
        await repo.InsertAsync(b);
        await repo.TryPlaceBidAsync((await repo.GetByUidAsync(b.Uid))!, (uint)Key(), "A", Key(), 200, now);
        var readB = await repo.GetByUidAsync(b.Uid);
        Assert.Null(await repo.TryAcceptTopBidAsync(readB!, seller + 1, now));

        
        var c = NewActive(seller, (uint)Key(), 10, 0, now, now.AddHours(1));
        await repo.InsertAsync(c);
        await repo.TryPlaceBidAsync((await repo.GetByUidAsync(c.Uid))!, (uint)Key(), "A", Key(), 200, now);
        var staleRead = await repo.GetByUidAsync(c.Uid);
        await repo.TryPlaceBidAsync(staleRead!, (uint)Key(), "B", Key(), 300, now); 
        Assert.Null(await repo.TryAcceptTopBidAsync(staleRead!, seller, now));
    }

    [Fact]
    public async Task GetTopBids_and_count_newest_first_excludes_expired()
    {
        if (!_fx.Available) return;
        var repo = new AuctionRepository(_fx.Db!);
        var now = DateTime.UtcNow;
        var bidder = (uint)Key();

        var older = NewActive((uint)Key(), (uint)Key(), 1, 0, now.AddMinutes(-10), now.AddHours(1));
        older.TopBidderCharId = bidder;
        older.TopBid = 100;
        older.TopBidderName = "me";
        older.TopBidderCharUid = Key();
        var newer = NewActive((uint)Key(), (uint)Key(), 1, 0, now.AddMinutes(-1), now.AddHours(1));
        newer.TopBidderCharId = bidder;
        newer.TopBid = 100;
        newer.TopBidderName = "me";
        newer.TopBidderCharUid = Key();
        var expired = NewActive((uint)Key(), (uint)Key(), 1, 0, now.AddMinutes(-5), now.AddHours(-1));
        expired.TopBidderCharId = bidder;
        expired.TopBid = 100;
        expired.TopBidderName = "me";
        expired.TopBidderCharUid = Key();

        await repo.InsertAsync(older);
        await repo.InsertAsync(newer);
        await repo.InsertAsync(expired);

        var top = await repo.GetTopBidsAsync(bidder, now);
        Assert.Equal(2, top.Count);
        Assert.Equal(newer.Uid, top[0].Uid); 
        Assert.Equal(older.Uid, top[1].Uid);
        Assert.Equal(2, await repo.CountTopBidsAsync(bidder, now));
    }
}
