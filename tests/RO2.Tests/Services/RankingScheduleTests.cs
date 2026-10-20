using System.Text.Json;
using RO2.Domain.Entities;
using RO2.Domain.GameData.Tables;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Services;

/// <summary>
/// The ranking calendar (RankingSchedule) and the board logic of RankingService: ranking, filtering and the self row.
/// </summary>
public class RankingScheduleTests
{
    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    private static DateTime Utc(int y, int mo, int d, int h, int mi) => new(y, mo, d, h, mi, 0, DateTimeKind.Utc);

    [Fact]
    public void ResetType_FollowsRankingJson_AndTypeOneIsDaily()
    {
        var table = LoadData<Ranking>("Ranking.json");
        Assert.Equal(RankingSchedule.ResetDaily, RankingSchedule.ResetTypeOf(table, 1));   
        Assert.Equal(RankingSchedule.ResetDaily, RankingSchedule.ResetTypeOf(table, 3));
        Assert.Equal(RankingSchedule.ResetDaily, RankingSchedule.ResetTypeOf(table, 4));
        Assert.Equal(RankingSchedule.ResetWeekly, RankingSchedule.ResetTypeOf(table, 10));
        Assert.Equal(RankingSchedule.ResetWeekly, RankingSchedule.ResetTypeOf(table, 110));
        Assert.Equal((byte)0, RankingSchedule.ResetTypeOf(table, 200));
    }

    [Fact]
    public void Daily_FlipsAtNoonUtc()
    {
        var daily = ResetRow(RankingSchedule.ResetDaily);
        
        Assert.Equal(Utc(2026, 8, 7, 12, 0), RankingSchedule.PeriodStart(daily, Utc(2026, 8, 8, 11, 51)));
        Assert.Equal(Utc(2026, 8, 8, 12, 0), RankingSchedule.PeriodStart(daily, Utc(2026, 8, 8, 12, 4)));
        Assert.Equal(Utc(2026, 8, 8, 12, 0), RankingSchedule.PeriodStart(daily, Utc(2026, 8, 8, 12, 0)));
        Assert.Equal(Utc(2026, 8, 8, 12, 0), RankingSchedule.NextResetAfter(daily, Utc(2026, 8, 8, 11, 51)));
        Assert.Equal(Utc(2026, 8, 9, 12, 0), RankingSchedule.NextResetAfter(daily, Utc(2026, 8, 8, 12, 0)));
        Assert.Equal(Utc(2026, 8, 7, 12, 0), RankingSchedule.PreviousPeriodStart(daily, Utc(2026, 8, 8, 12, 0)));
    }

    [Fact]
    public void Weekly_FlipsOnSundayNoonUtc()
    {
        var weekly = ResetRow(RankingSchedule.ResetWeekly);
        Assert.Equal("1:0:0:0:0:0:0", weekly.Reset_Week);
        
        Assert.Equal(Utc(2026, 7, 26, 12, 0), RankingSchedule.PeriodStart(weekly, Utc(2026, 8, 2, 11, 59)));
        Assert.Equal(Utc(2026, 8, 2, 12, 0), RankingSchedule.PeriodStart(weekly, Utc(2026, 8, 2, 12, 0)));
        Assert.Equal(Utc(2026, 8, 2, 12, 0), RankingSchedule.PeriodStart(weekly, Utc(2026, 8, 6, 3, 0)));
        Assert.Equal(Utc(2026, 8, 9, 12, 0), RankingSchedule.NextResetAfter(weekly, Utc(2026, 8, 2, 12, 0)));
        Assert.Equal(Utc(2026, 7, 26, 12, 0), RankingSchedule.PreviousPeriodStart(weekly, Utc(2026, 8, 2, 12, 0)));
    }

    [Fact]
    public void Filter_SplitsSelectionAndFilterType()
    {
        Assert.Equal(((byte)13, RankingSchedule.FilterByClass), RankingSchedule.DecodeFilter(0x35));
        Assert.Equal(((byte)0, RankingSchedule.FilterByProfession), RankingSchedule.DecodeFilter(0x02));
        Assert.Equal(((byte)0, (byte)0), RankingSchedule.DecodeFilter(0x00));
    }

 /// <summary>
 /// The type-3 board: the ten rows of the unfiltered 2964 and the five class-13 rows of the filtered one.
 /// </summary>
    private static List<RankingSnapshotRow> Type3Snapshot()
    {
        static RankingSnapshotRow R(uint id, uint rank, uint prev, byte cls, byte prof, long score, string name, string guild) =>
            new() { CharId = id, Rank = rank, PreviousRank = prev, ClassId = cls, Profession = prof, Score = score, Name = name, Guild = guild };
        return new()
        {
            R(1, 1, 60, 9, 2, 7223, "Eva1206", "Nemesis'"),
            R(2, 2, 4, 17, 4, 7044, "DNAFarmer4", "NekoNook"),
            R(3, 3, 3, 17, 2, 7041, "DNAFarmer3", "NekoNook"),
            R(4, 4, 5, 17, 2, 7032, "DNAFarmer2", "NekoNook"),
            R(5, 5, 2, 17, 2, 7006, "DNAFarmer1", "NekoNook"),
            R(6, 6, 0, 18, 4, 5373, "AuraMech", "LoneMyth"),
            R(7, 7, 6, 9, 2, 4946, "SLverz", "Nemesis'"),
            R(8, 8, 10, 11, 4, 3663, "Numbear", "NiteJester"),
            R(9, 9, 7, 7, 3, 3663, "PiroMarella", "NiteJester"),
            R(10, 10, 8, 8, 2, 3663, "SekkiHara", "NiteJester"),
            R(27, 27, 20, 13, 4, 783, "Nucleuz", ""),
            R(93, 93, 0, 13, 1, 81, "skuy1", ""),
            R(102, 102, 108, 13, 4, 73, "Namazuko", "WeAintFortless"),
            R(107, 107, 0, 13, 4, 67, "BrutalRogue", ""),
            R(114, 114, 0, 13, 3, 56, "RICHlGO", "WeAintFortless"),
        };
    }

    [Fact]
    public void Board_Unfiltered_IsTheTopTen()
    {
        
        const string retail2964 =
            "010a34000000010000003c0000000902371c0000000000000700450076006100310032003000360008004e0065006d0065007300" +
            "6900730027003a00000002000000040000001104841b0000000000000a0044004e0041004600610072006d006500720034000800" +
            "4e0065006b006f004e006f006f006b003a00000003000000030000001102811b0000000000000a0044004e004100460061007200" +
            "6d0065007200330008004e0065006b006f004e006f006f006b003a00000004000000050000001102781b0000000000000a004400" +
            "4e0041004600610072006d0065007200320008004e0065006b006f004e006f006f006b003a000000050000000200000011025e1b" +
            "0000000000000a0044004e0041004600610072006d0065007200310008004e0065006b006f004e006f006f006b00360000000600" +
            "0000000000001204fd14000000000000080041007500720061004d0065006300680008004c006f006e0065004d00790074006800" +
            "32000000070000000600000009025213000000000000060053004c007600650072007a0008004e0065006d006500730069007300" +
            "270038000000080000000a0000000b044f0e00000000000007004e0075006d0062006500610072000a004e006900740065004a00" +
            "6500730074006500720040000000090000000700000007034f0e0000000000000b005000690072006f004d006100720065006c00" +
            "6c0061000a004e006900740065004a00650073007400650072003c0000000a0000000800000008024f0e00000000000009005300" +
            "65006b006b00690048006100720061000a004e006900740065004a00650073007400650072000000";
        var board = RankingService.Board(Type3Snapshot(), 0x01);
        Assert.Equal(10, board.Count);
        Assert.Equal(retail2964, Hex(RankingPackets.Build2964(board)));
    }

    [Fact]
    public void Board_ClassFilter_KeepsGlobalRanks()
    {
        var board = RankingService.Board(Type3Snapshot(), 0x35);
        Assert.Equal(RankingPacketsTests.Retail2964Type3Class13, Hex(RankingPackets.Build2964(board)));
    }

    [Fact]
    public void Board_NoMatchingRow_IsRetailsEmptyBoard()
    {
        
        
        Assert.Equal(new[] { "skuy1" }, RankingService.Board(Type3Snapshot(), 0x06).Select(r => r.Name));
        Assert.Equal(new[] { "PiroMarella", "RICHlGO" }, RankingService.Board(Type3Snapshot(), 0x0e).Select(r => r.Name));
        Assert.Equal("01000100", Hex(RankingPackets.Build2964(RankingService.Board(Type3Snapshot(), 0x05))));
        Assert.Equal("01000100", Hex(RankingPackets.Build2964(RankingService.Board(null, 0x01))));
    }

    [Fact]
    public void SelfRanked_Unfiltered_IsTheGlobalRow()
    {
        
        const string retail2965 =
            "4000000072000000000000000d033800000000000000070052004900430048006c0047004f000e0057006500410069006e0074" +
            "0046006f00720074006c006500730073000000";
        var self = RankingService.SelfRanked(Type3Snapshot(), 114, 0x01);
        Assert.NotNull(self);
        Assert.Equal(retail2965, Hex(RankingPackets.Build2965(self.Value)));
    }

    [Fact]
    public void SelfRanked_ClassFilter_IsThePositionAmongFilteredRows()
    {
        
        const string retail2965 =
            "4000000005000000000000000d033800000000000000070052004900430048006c0047004f000e0057006500410069006e0074" +
            "0046006f00720074006c006500730073000000";
        var self = RankingService.SelfRanked(Type3Snapshot(), 114, 0x35);
        Assert.NotNull(self);
        Assert.Equal(retail2965, Hex(RankingPackets.Build2965(self.Value)));
    }

    [Fact]
    public void SelfRanked_NotOnTheBoard_IsNull()
    {
        Assert.Null(RankingService.SelfRanked(Type3Snapshot(), 999, 0x01));
        Assert.Null(RankingService.SelfRanked(Type3Snapshot(), 114, 0x45)); 
        Assert.Null(RankingService.SelfRanked(null, 114, 0x01));
    }

    [Fact]
    public void RankRows_SortsByScore_TiesByEarliestReached_PrevFromReplacedBoard()
    {
        var t0 = Utc(2026, 8, 8, 13, 0);
        var standings = new[]
        {
            new RankingService.Standing(10, 30, t0.AddMinutes(5), "B", 6, 1, "G"),
            new RankingService.Standing(11, 64, t0, "A", 12, 2, "LoneMyth"),
            new RankingService.Standing(12, 30, t0.AddMinutes(1), "C", 7, 3, ""),   
            new RankingService.Standing(13, 0, t0, "Z", 8, 4, ""),                   
        };
        var replaced = new List<RankingSnapshotRow>
        {
            new() { CharId = 11, Rank = 2 },
            new() { CharId = 10, Rank = 83 },
        };
        var rows = RankingService.RankRows(standings, replaced);
        Assert.Equal(new uint[] { 11, 12, 10 }, rows.Select(r => r.CharId));
        Assert.Equal(new uint[] { 1, 2, 3 }, rows.Select(r => r.Rank));
        Assert.Equal(new uint[] { 2, 0, 83 }, rows.Select(r => r.PreviousRank));
        Assert.Equal("LoneMyth", rows[0].Guild);
        Assert.Equal((byte)12, rows[0].ClassId);
        Assert.Equal((byte)2, rows[0].Profession);
    }

    [Fact]
    public void Feeds_OnlyTypesOneThreeAndFour()
    {
        Assert.Equal(RankingService.Feed.Counter, RankingService.FeedOf(1));
        Assert.Equal(RankingService.Feed.Counter, RankingService.FeedOf(3));
        Assert.Equal(RankingService.Feed.Counter, RankingService.FeedOf(4));
        foreach (byte t in new byte[] { 2, 8, 9, 10, 11, 12, 108, 109, 110 })
            Assert.Equal(RankingService.Feed.None, RankingService.FeedOf(t));
    }

    [Fact]
    public void CardCollector_CountsNewCardRowsOnly()
    {
        
        
        
        var cards = LoadData<CardInfo>("CardInfo.json").Select(c => c.Card_ID).ToHashSet();
        Assert.Equal(1, RankingService.CardRowsToCount(22310012, 1, cards));
        Assert.Equal(0, RankingService.CardRowsToCount(22310012, 0, cards));   
        Assert.Equal(0, RankingService.CardRowsToCount(11111111, 1, cards));   
    }

    [Fact]
    public void CardCollector_NewRowsOfALanding_AreTheStacksHoldingNoMoreThanTheLandedCount()
    {
        
        Assert.Equal(1, RankingService.NewRowsOf(new[] { Stack(1) }, 1));
        
        Assert.Equal(0, RankingService.NewRowsOf(new[] { Stack(4) }, 1));
        
        Assert.Equal(3, RankingService.NewRowsOf(new[] { Stack(10), Stack(10), Stack(5) }, 25));
        
        Assert.Equal(0, RankingService.NewRowsOf(Array.Empty<InventoryItem>(), 1));
        Assert.Equal(0, RankingService.NewRowsOf(new[] { Stack(1) }, 0));

        static InventoryItem Stack(int count) => new() { ItemId = 22310012, Count = count, StorageType = 0 };
    }

    private static RankingReset ResetRow(byte resetType) =>
        LoadData<RankingReset>("Ranking_Reset.json").Single(r => r.Reset_Type == resetType);

    private static List<T> LoadData<T>(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var path = Path.Combine(dir.FullName, "data", file);
            if (File.Exists(path))
                return JsonSerializer.Deserialize<List<T>>(File.ReadAllText(path))!;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("no ancestor of " + AppContext.BaseDirectory + " holds data/" + file);
    }
}
