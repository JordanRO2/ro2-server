using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Record order of 2456 OnAuctionSearch answers. Each case is one page in the expected order, as
/// (template id, English display name, start price, instant price, article uid). The comparator is fed the page
/// reversed and must restore the expected order.
/// </summary>
public class AuctionSearchOrderTests
{
    private const ulong ArticleBase = 1_040_000_000_000_000_000UL;

    private sealed record Row(uint Template, string Name, long Start, long Instant, ulong Uid);

    private static void AssertRetailOrder(AuctionPackets.SearchOrder order, Row[] retail)
    {
        
        var names = retail.DistinctBy(r => r.Template).ToDictionary(r => r.Template, r => r.Name);
        var keys = Enumerable.Reverse(retail)
            .Select(r => new AuctionPackets.SearchKey(r.Uid - ArticleBase, r.Template, r.Start, r.Instant));
        var sorted = AuctionPackets.OrderSearchKeys(keys, order, id => names[id]);
        Assert.Equal(retail.Select(r => r.Uid - ArticleBase), sorted.Select(k => k.Id));
    }

    [Fact]
    public void DefaultOrder_IsDisplayNameOrdinalThenStartThenInstantPrice()
    {
        
        
        
        AssertRetailOrder(AuctionPackets.SearchOrder.Default, new[]
        {
            new Row(22401966, "Gorem DNA fragment", 9800000100, 9800000550, 1040000000024636863),
            new Row(22401966, "Gorem DNA fragment", 9800000100, 9800000550, 1040000000024636862),
            new Row(22401966, "Gorem DNA fragment", 9800000100, 9800000550, 1040000000024636861),
            new Row(22401966, "Gorem DNA fragment", 9800000100, 9800000550, 1040000000024636860),
            new Row(22401966, "Gorem DNA fragment", 9800000100, 9800000980, 1040000000024636859),
            new Row(34000370, "HP Honing Polish (50%)", 50100, 50101, 1040000000024637310),
            new Row(34000370, "HP Honing Polish (50%)", 50100, 50101, 1040000000024637309),
            new Row(34000370, "HP Honing Polish (50%)", 50100, 50101, 1040000000024637308),
            new Row(34000370, "HP Honing Polish (50%)", 50100, 50101, 1040000000024637307),
            new Row(34000381, "Haste Honing Polish (50%)", 50100, 50101, 1040000000024637328),
        });
    }

    [Fact]
    public void DefaultOrder_GroupsByNameNotTemplateId()
    {
        
        
        AssertRetailOrder(AuctionPackets.SearchOrder.Default, new[]
        {
            new Row(22281304, "Abyssal Crystal", 200000100, 350000295, 1040000000024636022),
            new Row(33000715, "Aura of Swamp", 2220100, 3333990, 1040000000024636030),
            new Row(33000530, "Bell Collar Ornament", 50000, 50000, 1040000000024635862),
            new Row(33000618, "Bifrost Powder", 250000100, 250000255, 1040000000024635789),
            new Row(33000594, "Evil Spirit's Skull", 250000100, 250000255, 1040000000024635925),
            new Row(33000540, "Fragmented Black Horn", 100000, 100000, 1040000000024635779),
            new Row(33000616, "Kiss Mark of Aromine", 250000100, 250000255, 1040000000024635926),
            new Row(33000528, "Leviathan's Heart", 50000, 50000, 1040000000024635861),
            new Row(32200033, "Mastela Fruit", 1500000100, 3000000297, 1040000000024636012),
            new Row(33000511, "Monster Fragment(I)", 220000110, 300000990, 1040000000024636014),
        });
    }

    [Fact]
    public void StartPriceOrder_IsHighestFirst()
    {
        
        AssertRetailOrder(AuctionPackets.SearchOrder.StartPriceDesc, new[]
        {
            new Row(16702304, "Serenia's Handle", 10000000000, 10000000000, 1040000000024636977),
            new Row(16702295, "Serenia's Bow", 9987654000, 9987654000, 1040000000024637342),
            new Row(16702294, "Serenia's Staff", 8888888100, 8888888101, 1040000000024637676),
            new Row(16701520, "Osiris Staff", 5555555100, 5555555101, 1040000000024637677),
            new Row(16701527, "Osiris Quiver", 2185455100, 3084555000, 1040000000024637236),
            new Row(16700527, "Joser Quiver", 2184555100, 3084545000, 1040000000024637238),
            new Row(16702200, "Ignatius Quiver", 1000000000, 1000000001, 1040000000024637256),
            new Row(16701799, "Eddga Scythe", 350000100, 470000000, 1040000000024636957),
            new Row(16702160, "Cazar Doll", 100000100, 100000101, 1040000000024636905),
            new Row(16701793, "Eddga Mace", 35000100, 50000000, 1040000000024637242),
        });
    }

    [Fact]
    public void InstantPriceOrder_IsHighestFirstThenName()
    {
        
        
        AssertRetailOrder(AuctionPackets.SearchOrder.InstantPriceDesc, new[]
        {
            new Row(22310839, "Giant Forest's Red Poisonous Bug Card", 3000000100, 3000000999, 1040000000024636009),
            new Row(22310840, "Giant Forest's White Poisonous Bug Card", 3000000100, 3000000101, 1040000000024636150),
            new Row(22310838, "Giant Forest Ducuri Card", 1000000100, 1000000101, 1040000000024635904),
            new Row(22310792, "Mummy Card", 200000100, 200000101, 1040000000024636024),
            new Row(22310848, "Ttoki's Follower I Card", 55000110, 110000101, 1040000000024635790),
            new Row(22310795, "Isis Card", 40000100, 40000101, 1040000000024636142),
            new Row(22310790, "Muca Card", 4000100, 4000101, 1040000000024635918),
            new Row(22310785, "Condor Card", 2000100, 2000101, 1040000000024635916),
            new Row(22310786, "Gorem Card", 2000100, 2000101, 1040000000024635917),
            new Row(22310791, "Shadow of Sand card", 1000100, 1000101, 1040000000024636069),
        });
    }

    [Fact]
    public void PageMembership_FollowsTheNameOrderAcrossPages()
    {
        
        
        var names = new Dictionary<uint, string> { [100] = "Zzz", [200] = "Aaa", [300] = "Mid" };
        var keys = new List<AuctionPackets.SearchKey> { new(1, 100, 5, 5) };
        for (ulong i = 0; i < 9; i++) keys.Add(new(10 + i, 200, 5, 5));
        keys.Add(new(99, 300, 5, 5));
        var sorted = AuctionPackets.OrderSearchKeys(keys, AuctionPackets.SearchOrder.Default, id => names[id]);
        var page1 = sorted.Take(AuctionPackets.PageSize).ToList();
        var page2 = sorted.Skip(AuctionPackets.PageSize).ToList();
        Assert.Contains(page1, k => k.ItemId == 300);
        Assert.Single(page2);
        Assert.Equal(100u, page2[0].ItemId);
    }
}
