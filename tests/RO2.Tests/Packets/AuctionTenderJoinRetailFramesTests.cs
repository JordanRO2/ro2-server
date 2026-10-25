using RO2.Economy.Domain;
using System.Buffers.Binary;
using RO2.Domain.Entities;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Wire tests for the auction bid family: 1407 ReqAuctionTenderJoin and 2462 OnAuctionTenderJoin, the
/// minimum-bid rule, and the bid fields of AuctionEntry210. The 1404 and 2459 layouts are covered by
/// layout tests only, since no reference frame exists for them. Bodies are the RMI body after the opcode.
/// </summary>
public class AuctionTenderJoinRetailFramesTests
{
    private static byte[] Hex(string s) => Convert.FromHexString(s);

    
    
    public static readonly TheoryData<string, string> RetailBids = new()
    {
        { "012424006ee3119f8502a006e0677dd26e0ed5d3d90da83d040e68d4fe00ee778e0600000000",
          "012a2a006fe3119f85020000a006e0677dd26e0ed5d3d90da83d040eee778e060000000043150e1c02000000" },
        { "012424006ee3114e85038706e0677dd26e0e8dba7f0ea83d040e8dc4c901b950360700000000",
          "012a2a006fe3114e850300008706e0677dd26e0e8dba7f0ea83d040eb9503607000000009b2cd94601000000" },
        { "012424006ee3119368045e06e0677dd26e0e09a6cc0ea83d040e0e202c04ee778e0600000000",
          "012a2a006fe31193680400005e06e0677dd26e0e09a6cc0ea83d040eee778e06000000004fbb4cac00000000" },
        { "012424006ee31199e104b106e0677dd26e0ea971c80ea83d040e2cd9fe00f055c82000000000",
          "012a2a006fe31199e1040000b106e0677dd26e0ea971c80ea83d040ef055c82000000000aff20d6c01000000" },
        { "012424006ee31199e1042508e0677dd26e0ec072c80ea83d040e2dd9fe002e3c470300000000",
          "012a2a006fe31199e10400002508e0677dd26e0ec072c80ea83d040e2e3c47030000000081b6c66801000000" },
        { "012424006ee31199e1042408e0677dd26e0e9571c80ea83d040e2dd9fe002e3c470300000000",
          "012a2a006fe31199e10400002408e0677dd26e0e9571c80ea83d040e2e3c470300000000537a7f6501000000" },
        { "012424006ee311a1be05e10be0677dd26e0e2cb5080fa83d040eefd9fe00de64080000000000",
          "012a2a006fe311a1be050000e10be0677dd26e0e2cb5080fa83d040ede6408000000000047533fac00000000" },
    };

    [Fact]
    public void Request1407_FirstRetailBid_Parses()
    {
        Assert.True(AuctionPackets.TryParseTenderJoin(Hex(
            "012424006ee3119f8502a006e0677dd26e0ed5d3d90da83d040e68d4fe00ee778e0600000000"), out var r));
        Assert.Equal(1_040_000_000_024_643_232UL, r.ArticleUid); 
        Assert.Equal(1_010_000_008_434_013_141UL, r.ItemUid);    
        Assert.Equal(16_700_520u, r.ItemId);
        Assert.Equal(110_000_110UL, r.Bid);
    }

 /// <summary>
 /// Rebuilds each 2462 from its 1407 request (handle, uids, bid) and the wallet balance at offset 34,
 /// with result 0, and requires the frame to match byte for byte.
 /// </summary>
    [Theory]
    [MemberData(nameof(RetailBids))]
    public void Answer2462_RebuiltFromTheRequest_IsByteExact(string request, string answer)
    {
        var req = Hex(request);
        var ans = Hex(answer);
        Assert.True(AuctionPackets.TryParseTenderJoin(req, out var r));
        uint handle = BinaryPrimitives.ReadUInt32LittleEndian(req.AsSpan(2 + 4));
        long wallet = (long)BinaryPrimitives.ReadUInt64LittleEndian(ans.AsSpan(2 + 34));

        var built = AuctionPackets.BuildTenderJoin2462(handle, 0, r.ArticleUid, r.ItemUid, r.Bid, wallet);

        Assert.Equal(answer, Convert.ToHexString(built).ToLowerInvariant());
    }

    [Fact]
    public void Answer2462_WalletAt34_DropsByEachBid()
    {
        
        
        long[] wallets =
        {
            (long)BinaryPrimitives.ReadUInt64LittleEndian(Hex("012a2a006fe31199e1040000b106e0677dd26e0ea971c80ea83d040ef055c82000000000aff20d6c01000000").AsSpan(36)),
            (long)BinaryPrimitives.ReadUInt64LittleEndian(Hex("012a2a006fe31199e10400002508e0677dd26e0ec072c80ea83d040e2e3c47030000000081b6c66801000000").AsSpan(36)),
            (long)BinaryPrimitives.ReadUInt64LittleEndian(Hex("012a2a006fe31199e10400002408e0677dd26e0e9571c80ea83d040e2e3c470300000000537a7f6501000000").AsSpan(36)),
        };
        Assert.Equal(new long[] { 6_107_820_719, 6_052_820_609, 5_997_820_499 }, wallets);
        Assert.Equal(55_000_110, wallets[0] - wallets[1]);
        Assert.Equal(55_000_110, wallets[1] - wallets[2]);
    }

    [Fact]
    public void Request1407_WrongTagOrShortBlob_IsRejected()
    {
        
        Assert.False(AuctionPackets.TryParseTenderJoin(Hex(
            "012424006ce3119f8502a006e0677dd26e0ed5d3d90da83d040e68d4fe00ee778e0600000000"), out _));
        Assert.False(AuctionPackets.TryParseTenderJoin(Hex("0104040000006ee3"), out _));
    }

 /// <summary>
 /// Minimum-bid boundary cases for articles that already have a bidder: the price key before and after a
 /// bid equal to the minimum. Single-precision rounding at the boundary is covered: 990,000,108 is accepted,
 /// which a double-precision minimum (990,000,111) would refuse.
 /// </summary>
    [Theory]
    [InlineData(100L, 110L)]
    [InlineData(133L, 146L)]
    [InlineData(300_000_100L, 330_000_110L)]
    [InlineData(900_000_100L, 990_000_108L)]
    [InlineData(500_000_100L, 550_000_112L)]
    [InlineData(444_444_100L, 488_888_512L)]
    public void MinimumBid_MatchesTheArchiveBidSteps(long current, long next)
    {
        Assert.Equal(next, AuctionPackets.MinimumBid(current));
    }

    [Fact]
    public void MinimumBid_IsAtLeastOneAbove()
    {
        
        Assert.Equal(2L, AuctionPackets.MinimumBid(1));
        Assert.Equal(10L, AuctionPackets.MinimumBid(9));
        Assert.Equal(11L, AuctionPackets.MinimumBid(10));
    }

    private static AuctionArticle Article(long start = 100, long instant = 1_000, uint seller = 7,
        uint topBidder = 0, long topBid = 0) => new()
    {
        Id = 1, Uid = AuctionPackets.MakeArticleUid(1), SellerCharId = seller, SellerName = "Hanea",
        SellerCharUid = 1_000_000_000_008_520_457UL, ItemUid = 1_010_000_008_434_013_141UL, ItemId = 16_700_520,
        Count = 1, StartPrice = start, BuyoutPrice = instant,
        StartedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddHours(168),
        TopBidderCharId = topBidder, TopBid = topBid, TopBidderName = topBidder != 0 ? "Bidder" : string.Empty,
        TopBidderCharUid = topBidder != 0 ? 1_000_000_000_008_731_273UL : 0,
    };

    private static AuctionPackets.TenderJoinRequest Bid(AuctionArticle a, ulong bid) => new(a.Uid, a.ItemUid, a.ItemId, bid);

    [Fact]
    public void CheckBid_AppliesTheClientRules()
    {
        const uint me = 42;
        var a = Article();
        Assert.Equal((ushort)0, AuctionService.CheckBid(a, me, Bid(a, 110), 1_000_000, 0));        
        Assert.Equal((ushort)0, AuctionService.CheckBid(a, me, Bid(a, 500), 1_000_000, 0));        
        Assert.Equal((ushort)0, AuctionService.CheckBid(a, me, Bid(a, 1_000), 1_000_000, 0));      
        Assert.Equal(AuctionPackets.ResultBidItemMismatch, AuctionService.CheckBid(a, me, Bid(a, 109), 1_000_000, 0));   
        Assert.Equal(AuctionPackets.ResultBidItemMismatch, AuctionService.CheckBid(a, me, Bid(a, 1_001), 1_000_000, 0)); 
        Assert.Equal(AuctionPackets.ResultBidItemMismatch, AuctionService.CheckBid(a, a.SellerCharId, Bid(a, 110), 1_000_000, 0)); 
        Assert.Equal(AuctionPackets.ResultBidItemMismatch,
            AuctionService.CheckBid(a, me, new AuctionPackets.TenderJoinRequest(a.Uid, a.ItemUid + 1, a.ItemId, 110), 1_000_000, 0));
        Assert.Equal(AuctionPackets.ResultBidFailed, AuctionService.CheckBid(a, me, Bid(a, 110), 1_000_000, AuctionPackets.MaxTopBids)); 
        Assert.Equal(AuctionPackets.ResultNotEnoughMoney, AuctionService.CheckBid(a, me, Bid(a, 110), 109, 0));        
    }

    [Fact]
    public void CheckBid_WithATopBid_RaisesTheMinimumAndRefusesTheTopBidder()
    {
        const uint me = 42;
        var a = Article(start: 100, instant: 5_000, topBidder: 9, topBid: 121);
        Assert.Equal(AuctionPackets.ResultBidItemMismatch, AuctionService.CheckBid(a, me, Bid(a, 132), 1_000_000, 0));
        Assert.Equal((ushort)0, AuctionService.CheckBid(a, me, Bid(a, 133), 1_000_000, 0));
        Assert.Equal(AuctionPackets.ResultBidItemMismatch, AuctionService.CheckBid(a, 9, Bid(a, 133), 1_000_000, 0)); 
    }

    [Fact]
    public void Entry210_CarriesTheTopBid()
    {
        var now = DateTime.UtcNow;
        var bidded = Article(start: 100, instant: 1_000, topBidder: 9, topBid: 121);
        var search = AuctionPackets.BuildEntry210(bidded, now, withSellerName: true);
        var own = AuctionPackets.BuildEntry210(bidded, now, withSellerName: false);
        Assert.Equal(AuctionPackets.EntryLength, search.Length);

        Assert.Equal(121UL, BinaryPrimitives.ReadUInt64LittleEndian(search.AsSpan(134)));     
        Assert.Equal(1_000UL, BinaryPrimitives.ReadUInt64LittleEndian(search.AsSpan(142)));   
        Assert.Equal(bidded.TopBidderCharUid, BinaryPrimitives.ReadUInt64LittleEndian(search.AsSpan(202)));
        Assert.Equal("Hanea", Name150(search));   
        Assert.Equal("Bidder", Name150(own));     

        var fresh = Article();
        var freshOwn = AuctionPackets.BuildEntry210(fresh, now, withSellerName: false);
        Assert.Equal(100UL, BinaryPrimitives.ReadUInt64LittleEndian(freshOwn.AsSpan(134)));
        Assert.Equal(string.Empty, Name150(freshOwn)); 
        Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(freshOwn.AsSpan(202)));

        static string Name150(byte[] e) => System.Text.Encoding.Unicode.GetString(e, 150, 42).TrimEnd('\0');
    }

 /// <summary>
 /// The public search's price key is the entry+134 field, which holds the top bid once an article has one.
 /// Ordering cases list articles in key order; a control keyed on the stored start price yields a different order.
 /// </summary>
    [Fact]
    public void SearchOrder46_KeysOnTheBid_AsRetailDoes()
    {
        const ulong articleBase = 1_040_000_000_000_000_000UL;
        (ulong Uid, uint Template, string Name, long Start, long Bid, long Instant)[] retail =
        [
            (1040000000024637897, 16702360, "Serenia's Blouse", 1000000100, 0, 1000000999),
            (1040000000024637498, 16702362, "Serenia's Arm Cover", 900000100, 990000108, 1500000000),
            (1040000000024637497, 16702158, "Cazar Velveteen Cap", 500000100, 0, 800000000),
            (1040000000024637491, 16702360, "Serenia's Blouse", 300000100, 330000110, 550000000),
            (1040000000024637494, 16702318, "Serenia's Robe Gloves", 330000100, 0, 450000000),
            (1040000000024637485, 16702312, "Serenia's Light Armor Pants", 300000100, 0, 550000000),
            (1040000000024637488, 16702361, "Serenia's Trouser", 300000100, 0, 550000000),
            (1040000000024637495, 16702361, "Serenia's Trouser", 200000100, 220000110, 280000000),
            (1040000000024637493, 16702318, "Serenia's Robe Gloves", 210000100, 0, 270000000),
            (1040000000024637482, 16702158, "Cazar Velveteen Cap", 200000100, 0, 350000000),
        ];
        var names = retail.DistinctBy(r => r.Template).ToDictionary(r => r.Template, r => r.Name);
        var articles = Enumerable.Reverse(retail).Select(r => new AuctionArticle
        {
            Id = r.Uid - articleBase, Uid = r.Uid, ItemId = r.Template, Count = 1,
            StartPrice = r.Start, BuyoutPrice = r.Instant,
            TopBidderCharId = r.Bid != 0 ? 9u : 0, TopBid = r.Bid,
        }).ToList();
        var retailIds = retail.Select(r => r.Uid - articleBase).ToList();

        var now = DateTime.UtcNow;
        foreach (var a in articles)
            Assert.Equal((ulong)AuctionPackets.SearchKeyOf(a).CurrentPrice,
                BinaryPrimitives.ReadUInt64LittleEndian(AuctionPackets.BuildEntry210(a, now, withSellerName: true).AsSpan(134)));

        var sorted = AuctionPackets.OrderSearchKeys(articles.Select(AuctionPackets.SearchKeyOf),
            AuctionPackets.SearchOrder.StartPriceDesc, id => names[id]);
        Assert.Equal(retailIds, sorted.Select(k => k.Id));

        var byStart = AuctionPackets.OrderSearchKeys(
            articles.Select(a => new AuctionPackets.SearchKey(a.Id, a.ItemId, a.StartPrice, a.BuyoutPrice)),
            AuctionPackets.SearchOrder.StartPriceDesc, id => names[id]);
        Assert.NotEqual(retailIds, byStart.Select(k => k.Id));
    }

 /// <summary>
 /// Layout of 1404 as the client builder writes it: header <c></c>, then the handle and entry+0..15.
 /// Not verified against a reference frame.
 /// </summary>
    [Fact]
    public void Request1404_BuilderLayout_Parses()
    {
        var body = Hex("011818" + "0068e3" + "119f8502" + "a006e0677dd26e0e" + "d5d3d90da83d040e");
        Assert.True(AuctionPackets.TryParseSellTenderPrice(body, out var article, out var item));
        Assert.Equal(1_040_000_000_024_643_232UL, article);
        Assert.Equal(1_010_000_008_434_013_141UL, item);

        body[4] = 0x6A; 
        Assert.False(AuctionPackets.TryParseSellTenderPrice(body, out _, out _));
    }

 /// <summary>
 /// Layout of 2459 as the client consumer reads it: result u16 at blob+8, article dwords at blob+10 and
 /// blob+14, in the same envelope shape as 2460. Not verified against a reference frame; the msgTag 58217
 /// is the 1404 msgTag plus one.
 /// </summary>
    [Fact]
    public void Answer2459_PinnedToTheConsumerLayout()
    {
        var built = AuctionPackets.BuildSellTenderPrice2459(0x02859F11, 0, 1_040_000_000_024_643_232UL, 1_010_000_008_434_013_141UL);
        Assert.Equal("011a1a0069e3119f85020000a006e0677dd26e0ed5d3d90da83d040e", Convert.ToHexString(built).ToLowerInvariant());

        var cancel = AuctionPackets.BuildSellCancel2460(0x02859F11, 0, 1_040_000_000_024_643_232UL, 1_010_000_008_434_013_141UL);
        Assert.Equal(cancel.Length, built.Length);
        built[4] = cancel[4]; 
        Assert.Equal(cancel, built);

        var gone = AuctionPackets.BuildSellTenderPrice2459(1, AuctionPackets.ResultArticleGone, 5, 6);
        Assert.Equal(AuctionPackets.ResultArticleGone, BinaryPrimitives.ReadUInt16LittleEndian(gone.AsSpan(2 + 8)));
    }
}
