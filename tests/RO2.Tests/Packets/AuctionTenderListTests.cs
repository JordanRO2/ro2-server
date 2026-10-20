using System.Buffers.Binary;
using System.Text.Json;
using RO2.Domain.Entities;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Tests the two non-search auction lists: 2455 OnAuctionSellList (answer to 1401) and 2457
/// OnAuctionTenderList (answer to 1402), both ordered by
/// <see cref="AuctionPackets.OrderForList(IEnumerable{AuctionArticle}, Func{uint, string})"/>.
/// The 2455 cases are complete seller lists, each in the expected order as (template id, display name,
/// entry+134, entry+142, article uid). The helper receives them newest first, the order the store returns,
/// and must return the expected order. The 2457 cases cover the empty page only.
/// </summary>
public class AuctionTenderListTests
{
    private const ulong ArticleBase = 1_040_000_000_000_000_000UL;

    private sealed record Row(uint Template, string Name, long Current, long Instant, ulong Uid);

    private static AuctionArticle Article(Row r, ulong sellerUid, DateTime now) => new()
    {
        Id = r.Uid - ArticleBase,
        Uid = r.Uid,
        ItemId = r.Template,
        Count = 1,
        StartPrice = r.Current,
        BuyoutPrice = r.Instant,
        SellerCharUid = sellerUid,
        ExpiresAt = now.AddDays(7),
        State = AuctionArticleState.Active,
    };

 /// <summary>Input order of a seller list: the order the store returns it (newest first).</summary>
    private static List<AuctionArticle> NewestFirst(IEnumerable<Row> retail, ulong sellerUid, DateTime now)
        => retail.OrderByDescending(r => r.Uid).Select(r => Article(r, sellerUid, now)).ToList();

    private static Func<uint, string> Names(IEnumerable<Row> rows)
    {
        var names = rows.DistinctBy(r => r.Template).ToDictionary(r => r.Template, r => r.Name);
        return id => names[id];
    }

 /// <summary>
 /// Builds one 2455 page as AuctionSellListHandler does (ordering, Skip/Take, BuildEntry210, BuildList) and
 /// checks it against the expected frame: the list header byte for byte, then each entry's fields (+0 uid,
 /// +134, +142, +150 empty, +194 seller, +202 no bidder) in the order the list produces.
 /// </summary>
    private static void AssertSellPage(List<AuctionArticle> ordered, uint handle, ushort page, string retailHeaderHex,
        Row[] retailPage, ulong sellerUid, DateTime now)
    {
        var entries = ordered.Skip((page - 1) * AuctionPackets.PageSize).Take(AuctionPackets.PageSize)
            .Select(a => AuctionPackets.BuildEntry210(a, now, withSellerName: false)).ToList();
        var body = AuctionPackets.BuildList(AuctionPackets.MsgTagSellList, handle, page, ordered.Count, entries);

        var header = Convert.FromHexString(retailHeaderHex);
        Assert.Equal(retailHeaderHex, Convert.ToHexString(body.AsSpan(0, header.Length)).ToLowerInvariant());
        int blob = 1 + body[0];
        Assert.Equal(blob + 14 + AuctionPackets.EntryLength * retailPage.Length, body.Length);
        for (int i = 0; i < retailPage.Length; i++)
        {
            var e = body.AsSpan(blob + 14 + AuctionPackets.EntryLength * i, AuctionPackets.EntryLength);
            Assert.Equal(retailPage[i].Uid, BinaryPrimitives.ReadUInt64LittleEndian(e));
            Assert.Equal(retailPage[i].Template, BinaryPrimitives.ReadUInt32LittleEndian(e.Slice(8 + 8)));
            Assert.Equal((ulong)retailPage[i].Current, BinaryPrimitives.ReadUInt64LittleEndian(e.Slice(134)));
            Assert.Equal((ulong)retailPage[i].Instant, BinaryPrimitives.ReadUInt64LittleEndian(e.Slice(142)));
            Assert.True(e.Slice(150, 42).IndexOfAnyExcept((byte)0) < 0);
            Assert.Equal(sellerUid, BinaryPrimitives.ReadUInt64LittleEndian(e.Slice(194)));
            Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(e.Slice(202)));
        }
    }

    
    
    
    private const uint CazarHandle = 0x056A6511;
    private const ulong CazarSeller = 1_000_000_000_008_737_444UL;

    private static readonly Row[] CazarPage1 =
    {
        new(16702148, "Cazar Ascetic Tunic", 100, 1000, 1040000000024644692),
        new(16702147, "Cazar Bible", 100, 1000, 1040000000024644682),
        new(16702115, "Cazar Bow", 100, 1000, 1040000000024644671),
        new(16702115, "Cazar Bow", 100, 1000, 1040000000024644670),
        new(16702115, "Cazar Bow", 100, 1000, 1040000000024644669),
        new(16702098, "Cazar Breastplate", 100, 1000, 1040000000024644691),
        new(16702098, "Cazar Breastplate", 100, 1000, 1040000000024644690),
        new(16702111, "Cazar Coat Pants", 100, 1000, 1040000000024644694),
        new(16702159, "Cazar Handle", 100, 1000, 1040000000024644681),
        new(16702159, "Cazar Handle", 100, 1000, 1040000000024644680),
    };

    private static readonly Row[] CazarPage2 =
    {
        new(16702128, "Cazar Knuckle", 100, 1000, 1040000000024644676),
        new(16702128, "Cazar Knuckle", 100, 1000, 1040000000024644675),
        new(16702143, "Cazar Leather Gloves", 100, 1000, 1040000000024644689),
        new(16702143, "Cazar Leather Gloves", 100, 1000, 1040000000024644688),
        new(16702143, "Cazar Leather Gloves", 100, 1000, 1040000000024644687),
        new(16702143, "Cazar Leather Gloves", 100, 1000, 1040000000024644686),
        new(16702145, "Cazar Leather Helmet", 1000000000, 1000000000, 1040000000024644697),
        new(16702146, "Cazar Mace", 100, 1000, 1040000000024644667),
        new(16702108, "Cazar Robe Hood", 100, 1000, 1040000000024644696),
        new(16702103, "Cazar Rod", 100, 1000, 1040000000024644673),
    };

    private static readonly Row[] CazarPage3 =
    {
        new(16702103, "Cazar Rod", 100, 1000, 1040000000024644672),
        new(16702103, "Cazar Rod", 100, 1000, 1040000000024644664),
        new(16702137, "Cazar Scaled Armguard", 100, 1000, 1040000000024644685),
        new(16702135, "Cazar Scaled Jacket", 100, 1000, 1040000000024644693),
        new(16702153, "Cazar Scythe", 100, 1000, 1040000000024644679),
        new(16702153, "Cazar Scythe", 100, 1000, 1040000000024644678),
        new(16702153, "Cazar Scythe", 100, 1000, 1040000000024644677),
        new(16702165, "Cazar Silky Cap", 100, 1000, 1040000000024644695),
        new(16702096, "Cazar Sword", 100, 1000, 1040000000024644666),
        new(16702096, "Cazar Sword", 100, 1000, 1040000000024644665),
    };

    [Fact]
    public void SellList_IsSortedWholeBeforeThePageIsCut()
    {
        
        
        
        var now = new DateTime(2026, 8, 9, 23, 22, 54, DateTimeKind.Utc);
        var all = CazarPage1.Concat(CazarPage2).Concat(CazarPage3).ToArray();
        var ordered = AuctionPackets.OrderForList(NewestFirst(all, CazarSeller, now), Names(all));

        Assert.Equal(all.Select(r => r.Uid), ordered.Select(a => a.Uid));
        AssertSellPage(ordered, CazarHandle, 1, "024208420863e311656a0501001e000a00", CazarPage1, CazarSeller, now);
        AssertSellPage(ordered, CazarHandle, 2, "024208420863e311656a0502001e000a00", CazarPage2, CazarSeller, now);
        AssertSellPage(ordered, CazarHandle, 3, "024208420863e311656a0503001e000a00", CazarPage3, CazarSeller, now);
    }

    [Fact]
    public void SellList_CurrentPriceComesBeforeTheNewestArticle()
    {
        
        
        
        
        
        var now = new DateTime(2026, 9, 27, 6, 47, 27, DateTimeKind.Utc);
        const ulong seller = 1_000_000_000_008_571_067UL;
        Row[] retail =
        {
            new(16701992, "Lich Ruby Ornaments", 10000100, 10000890, 1040000000024662021),
            new(16701576, "Osiris Ruby Necklace", 10000100, 10000100, 1040000000024662211),
            new(70001813, "RO1 Costume Random Box", 300000100, 300000101, 1040000000024662020),
            new(70001813, "RO1 Costume Random Box", 1500000100, 1500000101, 1040000000024662280),
            new(12711584, "RO1 Taekwon Style Costume (F)", 300000100, 300000101, 1040000000024662014),
            new(12711584, "RO1 Taekwon Style Costume (F)", 300000100, 300000101, 1040000000024662001),
            new(12711584, "RO1 Taekwon Style Costume (F)", 300000100, 300000101, 1040000000024662000),
        };
        var ordered = AuctionPackets.OrderForList(NewestFirst(retail, seller, now), Names(retail));
        AssertSellPage(ordered, 0x0EBB4411, 1, "02cc05cc0563e31144bb0e010007000700", retail, seller, now);
    }

    [Fact]
    public void SellList_InstantPriceIsTheThirdKey()
    {
        
        
        
        
        var now = new DateTime(2026, 8, 9, 4, 37, 56, DateTimeKind.Utc);
        const ulong seller = 1_000_000_000_008_640_897UL;
        Row[] retail =
        {
            new(22281304, "Abyssal Crystal", 100, 161800, 1040000000024638466),
            new(22281304, "Abyssal Crystal", 100, 206295, 1040000000024638465),
            new(33000714, "Aura of Ocean Island", 100, 101, 1040000000024638467),
            new(33000715, "Aura of Swamp", 100, 300, 1040000000024638463),
            new(33000715, "Aura of Swamp", 100, 990, 1040000000024638464),
            new(22300201, "Card Enhanced Stone", 100000000, 100000000, 1040000000024638461),
            new(60000730, "Lucky Powder", 1, 1, 1040000000024638460),
            new(33000713, "Queen's Aura", 100, 101, 1040000000024638462),
        };
        var ordered = AuctionPackets.OrderForList(NewestFirst(retail, seller, now), Names(retail));
        AssertSellPage(ordered, 0x0E43A211, 1, "029e069e0663e311a2430e010008000800", retail, seller, now);
    }

    [Fact]
    public void SellList_RowNamesAreTheDataTablesDisplayNames()
    {
        
        
        
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "data", "ItemInfo.json")))
            root = root.Parent;
        if (root == null)
            throw new DirectoryNotFoundException("no ancestor of " + AppContext.BaseDirectory + " holds data/ItemInfo.json");

        using var items = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root.FullName, "data", "ItemInfo.json")));
        using var strings = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root.FullName, "data", "strings", "string_item_name.json")));
        var nameIdByTemplate = new Dictionary<uint, uint>();
        foreach (var row in items.RootElement.EnumerateArray())
            if (row.TryGetProperty("ID", out var id) && row.TryGetProperty("String_Item_Name", out var nameId)
                && id.TryGetUInt32(out var template) && nameId.TryGetUInt32(out var stringId))
                nameIdByTemplate.TryAdd(template, stringId);

        var rows = CazarPage1.Concat(CazarPage2).Concat(CazarPage3)
            .Append(new Row(70001813, "RO1 Costume Random Box", 0, 0, 0))
            .Append(new Row(12711584, "RO1 Taekwon Style Costume (F)", 0, 0, 0))
            .Append(new Row(33000715, "Aura of Swamp", 0, 0, 0))
            .Append(new Row(22281304, "Abyssal Crystal", 0, 0, 0))
            .Append(new Row(60000730, "Lucky Powder", 0, 0, 0))
            .Append(new Row(33000713, "Queen's Aura", 0, 0, 0));
        foreach (var r in rows.DistinctBy(r => r.Template))
        {
            Assert.True(nameIdByTemplate.TryGetValue(r.Template, out var nameId), $"template {r.Template} not in ItemInfo");
            Assert.Equal(r.Name, strings.RootElement.GetProperty(nameId.ToString(System.Globalization.CultureInfo.InvariantCulture)).GetString());
        }
    }

    [Theory]
    
    [InlineData("010b0b0064e311f0410f010002", "010e0e0065e311f0410f010000000000")]
    
    [InlineData("010b0b0064e3113cc903010002", "010e0e0065e3113cc903010000000000")]
    
    [InlineData("010b0b0064e3114fc502010002", "010e0e0065e3114fc502010000000000")]
    
    
    [InlineData("010b0b0064e311896006010003", "010e0e0065e311896006010000000000")]
    public void TenderList_NoTopBids_IsRetailsEmptyPage(string requestHex, string retailAnswerHex)
    {
        var request = Convert.FromHexString(requestHex);
        Assert.True(AuctionPackets.TryParseListRequest(request, AuctionPackets.MsgTagReqTenderList, out var page));
        Assert.Equal(1, page);
        Assert.False(AuctionPackets.TryParseListRequest(request, AuctionPackets.MsgTagReqSellList, out _));

        
        
        var handle = BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(1 + request[0] + 4));
        var ordered = AuctionPackets.OrderForList(new List<AuctionArticle>(), _ => string.Empty);
        var entries = ordered.Skip((page - 1) * AuctionPackets.PageSize).Take(AuctionPackets.PageSize)
            .Select(a => AuctionPackets.BuildEntry210(a, DateTime.UtcNow, withSellerName: true)).ToList();
        var body = AuctionPackets.BuildList(AuctionPackets.MsgTagTenderList, handle, page, ordered.Count, entries);
        Assert.Equal(retailAnswerHex, Convert.ToHexString(body).ToLowerInvariant());
    }

    [Fact]
    public void SellListRequest_CarriesThePage()
    {
        
        Assert.True(AuctionPackets.TryParseListRequest(Convert.FromHexString("010b0b0062e311656a05020002"), AuctionPackets.MsgTagReqSellList, out var p2));
        Assert.Equal(2, p2);
        Assert.True(AuctionPackets.TryParseListRequest(Convert.FromHexString("010b0b0062e311656a05030002"), AuctionPackets.MsgTagReqSellList, out var p3));
        Assert.Equal(3, p3);
    }
}
