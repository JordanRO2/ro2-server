using RO2.Domain.Entities;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Verifies the sort-order byte of the auction search (1400), decoded as the bit field the server uses
/// (<see cref="AuctionPackets.SearchKeyComparer"/>): bits 5-7 select the column (0 name, 1 level,
/// 2 entry+134, 3 entry+142, 4 entry+192 minutes), bit c sets the descending direction of column c
/// (column 4 is inverted), and ties fall through the remaining selected columns and then the comparer's
/// tail (template id ascending, expiry descending, newest article first).
/// </summary>
/// <remarks>
/// Each case is one complete page of records in server order, given as (template, display name,
/// Require_Level, Item_Lv, entry+134, entry+142, entry+192 minutes, article uid). The comparator receives
/// the page reversed and must restore the expected order.
/// </remarks>
public class AuctionSearchSortBitsTests
{
    private const ulong ArticleBase = 1_040_000_000_000_000_000UL;

    private sealed record Row(uint Template, string Name, int RequireLevel, int ItemLv, long Current, long Instant, long Minutes, ulong Uid);

    private static List<AuctionPackets.SearchKey> Sort(AuctionPackets.SearchOrder order, Row[] retail)
    {
        var names = retail.DistinctBy(r => r.Template).ToDictionary(r => r.Template, r => r.Name);
        var levels = retail.DistinctBy(r => r.Template).ToDictionary(r => r.Template, r => (r.RequireLevel, r.ItemLv));
        
        
        var keys = Enumerable.Reverse(retail)
            .Select(r => new AuctionPackets.SearchKey(r.Uid - ArticleBase, r.Template, r.Current, r.Instant, r.Minutes));
        return AuctionPackets.OrderSearchKeys(keys, order, id => names[id], id => levels[id]);
    }

    private static void AssertRetailOrder(AuctionPackets.SearchOrder order, Row[] retail)
    {
        Assert.Equal(retail.Select(r => r.Uid - ArticleBase), Sort(order, retail).Select(k => k.Id));
        
        Assert.NotEqual(retail.Select(r => r.Uid - ArticleBase), Sort(AuctionPackets.SearchOrder.Default, retail).Select(k => k.Id));
    }

    [Fact]
    public void Order0x82_IsRemainingMinutesDescending()
    {
        
        
        
        
        AssertRetailOrder(AuctionPackets.SearchOrder.RemainingDesc, new[]
        {
            new Row(90000226, "Applied Element-Destruction", 0, 0, 300100, 300545, 6337, 1040000000024638620),
            new Row(90000231, "Applied Element - No Element", 0, 0, 900000100, 920000545, 5397, 1040000000024638535),
            new Row(90000231, "Applied Element - No Element", 0, 0, 900000100, 920000545, 5348, 1040000000024638531),
            new Row(90000231, "Applied Element - No Element", 0, 0, 1000000000, 1000000000, 2928, 1040000000024638377),
            new Row(90000224, "Applied Element-Earth", 0, 0, 20100, 20545, 2639, 1040000000024638295),
            new Row(90000224, "Applied Element-Earth", 0, 0, 11500100, 12500440, 48, 1040000000024638062),
            new Row(90000225, "Applied Element-Life", 0, 0, 11500100, 17550985, 48, 1040000000024638060),
            new Row(90000225, "Applied Element-Life", 0, 0, 26100, 36545, 43, 1040000000024638045),
        });
    }

    [Fact]
    public void Order0x92_IsRemainingMinutesAscending()
    {
        
        
        AssertRetailOrder(AuctionPackets.SearchOrder.RemainingAsc, new[]
        {
            new Row(16702307, "Serenia's Heavy Armor Pants", 50, 105, 5000000100, 5000001000, 9769, 1040000000024642558),
            new Row(16701807, "Eddga Silky Blouse", 50, 100, 20000100, 35000000, 9831, 1040000000024642564),
            new Row(16701804, "Eddga Velveteen Cap", 50, 100, 5000000, 5000000, 9836, 1040000000024642566),
            new Row(16701790, "Eddga Leather Gloves", 50, 100, 300000100, 300000101, 9860, 1040000000024642567),
            new Row(16702177, "Ignatius Chain Gauntlets", 50, 97, 1000000, 1000000, 9863, 1040000000024642574),
            new Row(16702191, "Ignatius Robe Sandal", 50, 97, 1000000, 1000000, 9863, 1040000000024642575),
            new Row(16701754, "Eddga Robe Sandal", 50, 100, 100, 50000, 9958, 1040000000024642614),
            new Row(16701746, "Eddga Plated Gaiters", 50, 100, 25000100, 50000000, 10076, 1040000000024642636),
            new Row(16701748, "Eddga Plated Boots", 50, 100, 1000100, 2000000, 10077, 1040000000024642637),
        });
    }

    [Fact]
    public void Order0x62_IsInstantPriceAscending()
    {
        
        
        
        AssertRetailOrder(AuctionPackets.SearchOrder.InstantPriceAsc, new[]
        {
            new Row(16701852, "Witch Coat Hood", 50, 90, 100000, 100000, 6372, 1040000000024643863),
            new Row(16701928, "Lich Robe Hood", 50, 90, 2000000, 2000000, 4299, 1040000000024643521),
            new Row(16255022, "Robe Hood of Resignation", 50, 50, 5555100, 13000672, 6140, 1040000000024643795),
            new Row(16701811, "Eddga Silky Cap", 50, 100, 111110100, 111111000, 9356, 1040000000024644209),
            new Row(16701811, "Eddga Silky Cap", 50, 100, 111111100, 111111111, 10076, 1040000000024644281),
            new Row(16702320, "Serenia's Robe Hood", 50, 105, 160000100, 250000000, 2878, 1040000000024643341),
            new Row(16701798, "Eddga Ascetic Hood", 50, 100, 500000000, 500000000, 5267, 1040000000024643729),
            new Row(16701780, "Eddga Uniform Hat", 50, 100, 500000000, 500000000, 2911, 1040000000024643348),
        });
    }

    [Fact]
    public void Order0x42_IsCurrentPriceAscending()
    {
        
        
        
        AssertRetailOrder(AuctionPackets.SearchOrder.StartPriceAsc, new[]
        {
            new Row(33000714, "Aura of Ocean Island", 0, 0, 100, 101, 9790, 1040000000024638467),
            new Row(33000713, "Queen's Aura", 0, 0, 100, 101, 9790, 1040000000024638462),
            new Row(32100023, "Large Iron Ore", 0, 0, 1100, 1101, 7134, 1040000000024638207),
            new Row(33000528, "Leviathan's Heart", 0, 0, 10000, 1000000, 6972, 1040000000024638199),
            new Row(33000555, "Rainbow Jellopy", 0, 0, 100100, 100500, 114, 1040000000024638410),
            new Row(33000608, "Pillarnium Powder for Crafting", 0, 0, 400100, 400101, 329, 1040000000024637537),
            new Row(33000552, "Broken Barrier Fragment Crystal", 0, 0, 1000010, 1000010, 115, 1040000000024638411),
            new Row(33000571, "Desert Scorpion Shell", 0, 0, 8500100, 10000101, 9953, 1040000000024638484),
            new Row(33000571, "Desert Scorpion Shell", 0, 0, 8500100, 10000101, 9953, 1040000000024638483),
            new Row(33000610, "Red Fang Fragment", 0, 0, 8500100, 10000101, 9953, 1040000000024638485),
        });
    }

    [Fact]
    public void Order0x03_IsNameDescending_ThenPriceAscending()
    {
        
        
        
        AssertRetailOrder(AuctionPackets.SearchOrder.NameDesc, new[]
        {
            new Row(16701849, "Witch Coat Pants", 50, 90, 100000, 100000, 10063, 1040000000024643867),
            new Row(16702361, "Serenia's Trouser", 50, 105, 3600000100, 5500000000, 6570, 1040000000024643343),
            new Row(16701982, "Lich Silky Trouser", 50, 90, 50000100, 100000750, 5484, 1040000000024643133),
            new Row(16701931, "Lich Coat Pants", 50, 90, 50000100, 100000750, 4220, 1040000000024642992),
            new Row(16701777, "Eddga Uniform Pants", 50, 100, 500000000, 500000000, 6601, 1040000000024643349),
            new Row(16701808, "Eddga Silky Trouser", 50, 100, 7000000100, 9500000000, 2642, 1040000000024642784),
            new Row(16701758, "Eddga Coat Pants", 50, 100, 50000100, 55000500, 4162, 1040000000024642987),
            new Row(16701758, "Eddga Coat Pants", 50, 100, 50000100, 100000000, 7471, 1040000000024643464),
        });
    }

    [Fact]
    public void Order0x15_NameTiesFollowTheirOwnPriceBit()
    {
        
        
        
        
        
        AssertRetailOrder((AuctionPackets.SearchOrder)0x15, new[]
        {
            new Row(13231380, "White Reindeer Backpack", 0, 0, 777777100, 777777101, 10024, 1040000000024644063),
            new Row(12710551, "Reed Pipe", 0, 0, 9500000000, 9500000000, 3963, 1040000000024643082),
            new Row(12710551, "Reed Pipe", 0, 0, 4500000100, 5000000101, 6127, 1040000000024643386),
            new Row(70000765, "Musketeer Hat (Male)", 0, 0, 1000000100, 1000000101, 3290, 1040000000024642990),
            new Row(12721167, "Monocular", 0, 0, 1000000100, 1500000101, 7471, 1040000000024643603),
            new Row(13231370, "Green Reindeer Backpack", 0, 0, 777770100, 777777101, 9243, 1040000000024643894),
            new Row(13231420, "Adorned Santa Beard", 0, 0, 777777100, 777777101, 9856, 1040000000024644046),
        });
    }

    [Fact]
    public void Order0x22_IsLevelDescending()
    {
        
        
        
        
        AssertRetailOrder(AuctionPackets.SearchOrder.LevelDesc, new[]
        {
            new Row(16702323, "Serenia's Earing", 50, 105, 2000000100, 2000001000, 10061, 1040000000024642529),
            new Row(16702252, "Ignatius Ruby Earing", 50, 97, 1500100, 1500101, 9943, 1040000000024642474),
            new Row(16702252, "Ignatius Ruby Earing", 50, 97, 2000100, 2000101, 6214, 1040000000024642136),
            new Row(16702255, "Ignatius Sapphire Earing", 50, 97, 2000100, 2000101, 9943, 1040000000024642475),
            new Row(16702255, "Ignatius Sapphire Earing", 50, 97, 2000100, 2000101, 6214, 1040000000024642137),
            new Row(16700481, "Desert Storm Sapphire Earings", 50, 85, 100, 11000, 2774, 1040000000024638644),
            new Row(16700481, "Desert Storm Sapphire Earings", 50, 85, 100, 222000, 1389, 1040000000024642247),
            new Row(16700178, "Desert Scorpion Ruby Earing", 50, 80, 100, 199000, 4219, 1040000000024642502),
        });
    }

    [Fact]
    public void Order0x20_IsLevelAscending()
    {
        
        
        
        
        AssertRetailOrder(AuctionPackets.SearchOrder.LevelAsc, new[]
        {
            new Row(11400531, "Billfrid's Dagger", 5, 5, 1100, 1192, 6171, 1040000000024638458),
            new Row(11400531, "Billfrid's Dagger", 5, 5, 1100, 1192, 6041, 1040000000024638455),
            new Row(11601511, "Wolf Stick", 15, 15, 110100, 111912, 334, 1040000000024638753),
            new Row(11201731, "Assault Staff", 17, 17, 1100, 1367, 6577, 1040000000024638487),
            new Row(11402031, "Dagger of Delusion", 20, 20, 5100, 5758, 7807, 1040000000024638566),
            new Row(16700126, "Desert Scorpion Bow", 50, 80, 40100, 50000, 8612, 1040000000024638726),
            new Row(16700126, "Desert Scorpion Bow", 50, 80, 40100, 50000, 8611, 1040000000024638725),
            new Row(16700126, "Desert Scorpion Bow", 50, 80, 40100, 50000, 7412, 1040000000024638546),
            new Row(16700126, "Desert Scorpion Bow", 50, 80, 150100, 300000, 7056, 1040000000024638518),
            new Row(16701817, "Menace Bible", 50, 80, 50000000, 50000000, 10016, 1040000000024638852),
        });
    }

    [Fact]
    public void DefaultOrder_KeepsTheOldKey()
    {
        
        
        
        var names = new Dictionary<uint, string> { [1] = "B", [2] = "A" };
        var keys = new[]
        {
            new AuctionPackets.SearchKey(10, 1, 5, 9, 100),
            new AuctionPackets.SearchKey(11, 2, 7, 1, 50),
            new AuctionPackets.SearchKey(12, 2, 7, 1, 60),
            new AuctionPackets.SearchKey(13, 2, 6, 8, 70),
            new AuctionPackets.SearchKey(14, 2, 7, 0, 70),
        };
        var sorted = AuctionPackets.OrderSearchKeys(keys, AuctionPackets.SearchOrder.Default, id => names[id]);
        Assert.Equal(new ulong[] { 13, 14, 12, 11, 10 }, sorted.Select(k => k.Id));
    }

    [Fact]
    public void UnseenColumn_FallsBackToTheName()
    {
        
        var names = new Dictionary<uint, string> { [1] = "B", [2] = "A" };
        var keys = new[] { new AuctionPackets.SearchKey(1, 1, 1, 1, 1), new AuctionPackets.SearchKey(2, 2, 9, 9, 9) };
        var sorted = AuctionPackets.OrderSearchKeys(keys, (AuctionPackets.SearchOrder)0xE2, id => names[id]);
        Assert.Equal(new ulong[] { 2, 1 }, sorted.Select(k => k.Id));
    }

    [Fact]
    public void SearchKeyOf_CarriesTheExpiryMinute_RemainingMinutesFloorsOn()
    {
        var now = new DateTime(2026, 9, 21, 13, 24, 46, DateTimeKind.Utc);
        var a = new AuctionArticle { Id = 7, ItemId = 1, StartPrice = 100, ExpiresAt = now.AddMinutes(10079).AddSeconds(10) };
        var b = new AuctionArticle { Id = 8, ItemId = 1, StartPrice = 100, ExpiresAt = now.AddMinutes(10079).AddSeconds(5) };
        
        
        Assert.Equal(AuctionPackets.RemainingMinutes(a, now), AuctionPackets.RemainingMinutes(b, now));
        Assert.Equal(AuctionPackets.SearchKeyOf(a).ExpiryMinute, AuctionPackets.SearchKeyOf(b).ExpiryMinute);
        Assert.Equal(a.ExpiresAt.Ticks / TimeSpan.TicksPerMinute, AuctionPackets.SearchKeyOf(a).ExpiryMinute);
        Assert.Equal(a.ExpiresAt.Ticks, AuctionPackets.SearchKeyOf(a).ExpiresTicks);
    }
}
