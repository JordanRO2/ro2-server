using RO2.Economy.Domain;
using System.Buffers.Binary;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Services;
using RO2.Domain.Entities;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Cash-mall purchase flow checks against reference frames: 1598 parsing and pricing, 2870, 2638, the
/// web-mall box lists 2639 and 2640, the claim 2641 and the history 2874.
/// </summary>
public class CashMallPacketsTests
{
    
    private const string Retail1598BuyHex =
        "3600000001002c2d2c04e30b00000000f0410100000000000000f041040031003200330034000000000000000000000000000000000000000000";
    
    private const string Retail2870BuyHex = "1a000000010000002c2d2c040100ffbfba0200000000000052440000000000";

    
    
    private const string Retail1598GiftHex =
        "5a00000001002d2d2c04e40b00000000f0410100000000000000f04104003100320033003400c413680dc161a637f276110000000c00" +
        "44006f006f006d00500072006f0074006f0063006f0049000600680065006c006c006f0036000000";
    
    private const string Retail2870GiftHex = "1a000000010000002d2d2c040100d6bfba020000000000007f440000000001";

    
    private const string Retail1598ZeroTotalHex =
        "360000000100403cf803000000000000000001000000000000000000040031003200330034000000000000000000000000000000000000000000";
    private const string Retail2870TotalMismatchHex = "0e0000000000935e0000000000000000000000";

    
    private const string Retail1598NotOfferedHex =
        "360000000100a7212c04000000000000000001000000000000000000040031003200330034000000000000000000000000000000000000000000";
    private const string Retail2870NotOfferedHex = "0e0000000000c20b0000000000000000000000";

    [Fact]
    public void ParsesRetailPurchaseAndGift()
    {
        Assert.True(GameMallPurchaseHandler.TryParse(Convert.FromHexString(Retail1598BuyHex), out var buy));
        var line = Assert.Single(buy.Lines);
        Assert.Equal((70004012u, 3043u, 30f, (ushort)1), (line.ItemId, line.ProductId, line.Total, line.Quantity));
        Assert.False(buy.IsGift);
        Assert.Equal("1234", buy.Password);

        Assert.True(GameMallPurchaseHandler.TryParse(Convert.FromHexString(Retail1598GiftHex), out var gift));
        line = Assert.Single(gift.Lines);
        Assert.Equal((70004013u, 3044u, 30f, (ushort)1), (line.ItemId, line.ProductId, line.Total, line.Quantity));
        Assert.True(gift.IsGift);
        Assert.Equal(0x1176f2u, gift.RecipientAccount);
        Assert.Equal(("", "DoomProtocoI", "hello6"), (gift.Message, gift.SenderName, gift.RecipientName));
    }

    [Fact]
    public void ValidatesLikeRetail()
    {
        var catalog = CashMallCatalog.Load(GameMall2866And2868Tests.CatalogPath());
        long now = new DateTimeOffset(2026, 9, 26, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

        Assert.True(GameMallPurchaseHandler.TryParse(Convert.FromHexString(Retail1598BuyHex), out var buy));
        Assert.Equal(0, GameMallPurchaseHandler.Validate(buy, catalog, now, out var priced));
        Assert.Equal(30L, Assert.Single(priced).Total);

        Assert.True(GameMallPurchaseHandler.TryParse(Convert.FromHexString(Retail1598ZeroTotalHex), out var zero));
        Assert.Equal(GameMallPurchaseHandler.CodeTotalMismatch, GameMallPurchaseHandler.Validate(zero, catalog, now, out _));

        Assert.True(GameMallPurchaseHandler.TryParse(Convert.FromHexString(Retail1598NotOfferedHex), out var other));
        Assert.Equal(GameMallPurchaseHandler.CodeNotOffered, GameMallPurchaseHandler.Validate(other, catalog, now, out _));

        
        Assert.Equal(GameMallPurchaseHandler.CodeNotOffered, GameMallPurchaseHandler.Validate(buy, null, now, out _));
        Assert.Equal(GameMallPurchaseHandler.CodeSalePeriodOver, GameMallPurchaseHandler.Validate(buy, catalog, 7_000_000_000L, out _));
    }

    [Fact]
    public void PurchaseAnswersAreRetailsBytes()
    {
        Assert.Equal(Convert.FromHexString(Retail2870BuyHex), CashMallPackets.Build2870Success(
            new[] { new CashMallPackets.Delivered(70004012, 1, 45_793_279) }, 840, gift: false));
        Assert.Equal(Convert.FromHexString(Retail2870GiftHex), CashMallPackets.Build2870Success(
            new[] { new CashMallPackets.Delivered(70004013, 1, 45_793_238) }, 1020, gift: true));
        Assert.Equal(Convert.FromHexString(Retail2870TotalMismatchHex),
            CashMallPackets.Build2870Refusal(GameMallPurchaseHandler.CodeTotalMismatch, gift: false));
        Assert.Equal(Convert.FromHexString(Retail2870NotOfferedHex),
            CashMallPackets.Build2870Refusal(GameMallPurchaseHandler.CodeNotOffered, gift: false));
    }

    [Fact]
    public void BoxNotifyCountsEntries()
    {
        
        Assert.Equal(Convert.FromHexString("010000000000"), WebMallNotifyPackets.Build2638(1, 0));
        Assert.Equal(Convert.FromHexString("000001000000"), WebMallNotifyPackets.Build2638(0, 1));
        Assert.Equal(new byte[6], WebMallNotifyPackets.Build2638(0, 0));

        var entries = new[]
        {
            new CashMallBoxEntry { EntryId = 1, Box = CashMallStore.BoxPurchased, ItemId = 70004012, Count = 2 },
            new CashMallBoxEntry { EntryId = 2, Box = CashMallStore.BoxPurchased, ItemId = 70004013, Count = 0 },
            new CashMallBoxEntry { EntryId = 3, Box = CashMallStore.BoxGift, ItemId = 70004013, Count = 5 },
        };
        Assert.Equal((1, 1), CashMallRules.BoxCounts(entries)); 
        Assert.Equal((0, 0), CashMallRules.BoxCounts(null));
    }

    [Fact]
    public void BoxListsAreRetailsBytes()
    {
        
        var retail2639 = Convert.FromHexString(
            "013900000100020002878e1c00012d2d2c04564a94335f9ae640000000005f9ae640868e1c00022c2d2c0403a8f7315f9ae640000000005f9ae640");
        var box1 = new[]
        {
            new CashMallPackets.ListedEntry(0x1c8e87, 1, 70004013, Ole("564a94335f9ae640"), ""),
            new CashMallPackets.ListedEntry(0x1c8e86, 2, 70004012, Ole("03a8f7315f9ae640"), ""),
        };
        Assert.Equal(retail2639, CashMallPackets.Build2639(0, 1, 2, box1));
        Assert.Equal(retail2639, CashMallPackets.BuildBoxListAnswer(CashMallStore.BoxPurchased, 0, box1)); 

        
        var retail2640 = Convert.FromHexString(
            "017301000100010002017a8e1c00022c2d2c046f74cfff5c9ae640000000005c9ae64044006f006f006d00500072006f0074006f0063006f" +
            "0049000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
            "0000000000");
        var box2 = new[]
        {
            new CashMallPackets.ListedEntry(0x1c8e7a, 2, 70004012, Ole("6f74cfff5c9ae640"), "DoomProtocoI"),
        };
        Assert.Equal(retail2640, CashMallPackets.Build2640(1, 1, 1, box2));
        Assert.Equal(retail2640, CashMallPackets.BuildBoxListAnswer(CashMallStore.BoxGift, 1, box2));

        
        var none = Array.Empty<CashMallPackets.ListedEntry>();
        Assert.Equal(Convert.FromHexString("010701000000000000"), CashMallPackets.BuildBoxListAnswer(CashMallStore.BoxPurchased, 1, none));
        Assert.Equal(Convert.FromHexString("01080000000000000200"), CashMallPackets.BuildBoxListAnswer(CashMallStore.BoxGift, 0, none));
    }

    
    
    
    private const string Retail8BoxPage1 =
        "02cf000100020008000894951a00011b8b93034e2844a8aef7e44000000000aef7e4407c7c1a00011c8b9303405a89a6aef7e44000000000" +
        "aef7e440a6491a0001f78993032d66acd598f7e4400000000098f7e4408e301a00018a8993038e65e7d298f7e4400000000098f7e4407617" +
        "1a0001c98793034012a7d198f7e4400000000098f7e4405efe19000115889303439982cc98f7e4400000000098f7e44046e51900014d8793" +
        "03ef4de6ca98f7e4400000000098f7e4402ecc19000176889303cfab62c998f7e4400000000098f7e440";
    private const string Retail8BoxPage2 =
        "0152020002000800035efe19000115889303439982cc98f7e4400000000098f7e44046e51900014d879303ef4de6ca98f7e4400000000098" +
        "f7e4402ecc19000176889303cfab62c998f7e4400000000098f7e440";

    
    
    private const string Retail6BoxPage1 =
        "029d0001000200060006c09b1a00011b8b9303c8a37ca8aef7e44000000000aef7e440a8821a00011c8b93034880e8a6aef7e44000000000" +
        "aef7e440d24f1a0001f78993030dc5e2d598f7e4400000000098f7e440a21d1a0001c98793032071ddd198f7e4400000000098f7e4408a04" +
        "1a00011588930365f4b6cc98f7e4400000000098f7e44072eb1900014d879303b94524cb98f7e4400000000098f7e440";
    private const string Retail6BoxPage2 = "01200200020006000172eb1900014d879303b94524cb98f7e4400000000098f7e440";

    
    
    private const string Retail7BoxPage1 =
        "02b60001000200070007b3a51a00011b8b9303cb3fd2a8aef7e44000000000aef7e4409b8c1a00011c8b9303eb746fa7aef7e44000000000" +
        "aef7e440c5591a0001f7899303efcb37d698f7e4400000000098f7e440ad401a00018a899303ba8573d398f7e4400000000098f7e4409527" +
        "1a0001c98793035a9836d298f7e4400000000098f7e4407d0e1a000115889303775d0dcd98f7e4400000000098f7e4404ddc190001768893" +
        "032bea1cca98f7e4400000000098f7e440";

    [Theory]
    [InlineData(Retail8BoxPage1, 1u, Retail8BoxPage1)]
    [InlineData(Retail8BoxPage1, 2u, Retail8BoxPage2)]
    [InlineData(Retail6BoxPage1, 1u, Retail6BoxPage1)]
    [InlineData(Retail6BoxPage1, 2u, Retail6BoxPage2)]
    [InlineData(Retail7BoxPage1, 1u, Retail7BoxPage1)]
    public void MultiPageBoxesAreRetailsBytes(string wholeBoxHex, uint page, string retailAnswerHex)
    {
        
        
        var wholeBox = ParseBox1Entries(Convert.FromHexString(wholeBoxHex));
        Assert.Equal(Convert.FromHexString(retailAnswerHex),
            CashMallPackets.BuildBoxListAnswer(CashMallStore.BoxPurchased, page, wholeBox));
    }

    [Fact]
    public void ListPagesOfFiveRunToTheEndOfTheBox()
    {
        
        
        Assert.Equal((0, 0, (ushort)0), CashMallPackets.ListPage(0, 1));
        Assert.Equal((0, 2, (ushort)1), CashMallPackets.ListPage(2, 0)); 
        Assert.Equal((0, 2, (ushort)1), CashMallPackets.ListPage(2, 1));
        Assert.Equal((0, 5, (ushort)1), CashMallPackets.ListPage(5, 1));
        Assert.Equal((0, 6, (ushort)2), CashMallPackets.ListPage(6, 1)); 
        Assert.Equal((5, 1, (ushort)2), CashMallPackets.ListPage(6, 2));
        Assert.Equal((0, 7, (ushort)2), CashMallPackets.ListPage(7, 1)); 
        Assert.Equal((5, 3, (ushort)2), CashMallPackets.ListPage(8, 2)); 
        Assert.Equal((10, 2, (ushort)3), CashMallPackets.ListPage(12, 3)); 
        Assert.Equal((9, 0, (ushort)2), CashMallPackets.ListPage(9, 5)); 
        Assert.Equal((0, CashMallPackets.ListMaxEntries, (ushort)60), CashMallPackets.ListPage(300, 1)); 
    }

 /// <summary>The 25-byte entries of a 2639 body: [CompactLen][u16 page][u16 pages][u16 total][u8 n] + n entries.</summary>
    private static List<CashMallPackets.ListedEntry> ParseBox1Entries(byte[] body)
    {
        int lenBytes = body[0];
        int p = 1 + lenBytes + 6;
        int n = body[p++];
        var entries = new List<CashMallPackets.ListedEntry>(n);
        for (int i = 0; i < n; i++, p += CashMallPackets.ListEntryBytes)
        {
            var e = body.AsSpan(p, CashMallPackets.ListEntryBytes);
            entries.Add(new CashMallPackets.ListedEntry(BinaryPrimitives.ReadUInt32LittleEndian(e), e[4],
                BinaryPrimitives.ReadUInt32LittleEndian(e[5..]), BinaryPrimitives.ReadDoubleLittleEndian(e[9..]), ""));
            
            Assert.Equal(CashMallPackets.HighDwordOnly(entries[^1].FiledOleDate), BinaryPrimitives.ReadDoubleLittleEndian(e[17..]));
        }
        Assert.Equal(body.Length, p);
        return entries;
    }

    [Fact]
    public void ClaimAnswersAreRetailsBytes()
    {
        
        var box1 = Convert.FromHexString(
            "858e1c00000000000001436587295f9ae640000000005f9ae640000000000000000000000000000000000000000000000000000000000000" +
            "0000000000000000000000007d68fa12a83d040e2d2d2c040000010407000000000000000000000000000000000000000000000000000000" +
            "0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
            "0000000000000000000000000000000000000000000000000000");
        Assert.Equal(CashMallPackets.ClaimBytes, box1.Length);
        Assert.Equal(box1, CashMallPackets.Build2641(0x1c8e85, CashMallStore.BoxPurchased, Ole("436587295f9ae640"), "", box1.AsSpan(68)));

        
        var box2 = Convert.FromHexString(
            "c18d1c000000000000022a3b4c9d5b98e640000000005b98e640740065006100730074006500720061000000000000000000000000000000" +
            "00000000000000000000000027d02711a83d040e4d3cf80300000e041a000000000000000000000000000000000000000000000000000000" +
            "0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
            "0000000000000000000000000000000000000000000000000000");
        Assert.Equal(CashMallPackets.ClaimBytes, box2.Length);
        Assert.Equal(box2, CashMallPackets.Build2641(0x1c8dc1, CashMallStore.BoxGift, Ole("2a3b4c9d5b98e640"), "teastera", box2.AsSpan(68)));
    }

    [Fact]
    public void HistoryIsRetailsBytes()
    {
        
        var retail = Convert.FromHexString(
            "0000003400000002000200000045222c04050000002041d8f83e540000000000000001000000674b5d0501000000c8422f8f3d5400000000000000");
        var ours = CashMallPackets.Build2874(0, new[]
        {
            new CashMallPackets.HistoryLine(2, 70001221, 5, 10, 0x543ef8d8, false, "", ""),
            new CashMallPackets.HistoryLine(1, 90000231, 1, 100, 0x543d8f2f, false, "", ""),
        });
        Assert.Equal(retail, ours);

        
        Assert.Equal(Convert.FromHexString("000001020000000000"), CashMallPackets.Build2874(1, Array.Empty<CashMallPackets.HistoryLine>()));
    }

    [Fact]
    public void HistoryListsTheNewestTwoHundredWithTheirSequence()
    {
        var purchases = Enumerable.Range(1, 341)
            .Select(i => new CashMallPurchase { ItemId = (uint)i, Quantity = 1, UnitPrice = 30, BoughtUtc = DateTime.UtcNow })
            .ToList();
        var lines = CashMallPackets.HistoryNewestFirst(purchases);

        Assert.Equal(CashMallPackets.HistoryMaxRecords, lines.Count);
        Assert.Equal((341u, 341u), (lines[0].Sequence, lines[0].ItemId));
        Assert.Equal((142u, 142u), (lines[^1].Sequence, lines[^1].ItemId)); 
        var body = CashMallPackets.Build2874(0, lines);
        Assert.Equal(200, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(7)));
    }

    [Fact]
    public void LargeQuantitiesSplitIntoByteSizedEntries()
    {
        var catalog = CashMallCatalog.Load(GameMall2866And2868Tests.CatalogPath());
        var product = catalog.FindByItem(70004012)!;
        var priced = new[] { new GameMallPurchaseHandler.PricedLine(product, 300) };

        Assert.Equal(2, GameMallPurchaseHandler.EntriesFor(300));
        var entries = GameMallPurchaseHandler.BuildEntries(priced, new uint[] { 7, 8 }, CashMallStore.BoxGift, DateTime.UtcNow, "Buyer");
        Assert.Equal(new[] { (7u, 255), (8u, 45) }, entries.Select(e => (e.EntryId, e.Count)));
        Assert.All(entries, e => Assert.Equal(("Buyer", CashMallStore.BoxGift), (e.SenderName, e.Box)));
    }

    [Fact]
    public void SecondListDateKeepsOnlyTheHighDword()
    {
        
        Assert.Equal(Ole("000000005f9ae640"), CashMallPackets.HighDwordOnly(Ole("564a94335f9ae640")));
    }

    private static double Ole(string littleEndianHex)
        => BinaryPrimitives.ReadDoubleLittleEndian(Convert.FromHexString(littleEndianHex));
}
