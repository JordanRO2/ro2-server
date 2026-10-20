using System.Text.Json;
using RO2.Domain.GameData;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>3027 NfyShowAttendanceState / 3026 AnsShowAttendanceState and the day's reward (<see cref="AttendanceDayGate"/>).</summary>
public class AttendancePopup3027Tests
{
 /// <summary>Day 1 of the attendance cycle.</summary>
    private const string RetailDay1 =
        "ad000000011c011c002001a58793030001da8893030001e688930300014c8793030001158893030001b0f4fa021001d9f4fa020001568793030002da8893030002e688930300014c8793030001158893030001b0f4fa021001daf4fa0200013a8793030003da8893030003e688930300014c8793030001158893030001b0f4fa021001dbf4fa020001418793030004da8893030004e688930300014c8793030001158893030001b0f4fa021001dcf4fa02";

 /// <summary>Day 17; the following gift page lists the reward (60000486 x3).</summary>
    private const string RetailDay17 =
        "ad000000011c111c002001a58793032001da8893032001e688930320014c8793032001158893032001b0f4fa023001d9f4fa022001568793032002da8893032002e688930320014c8793032001158893032001b0f4fa023001daf4fa0220013a8793032003da8893032003e688930300014c8793030001158893030001b0f4fa021001dbf4fa020001418793030004da8893030004e688930300014c8793030001158893030001b0f4fa021001dcf4fa02";

 /// <summary>Day 2, the following server day.</summary>
    private const string RetailDay2 =
        "ad000000011c021c002001a58793032001da8893030001e688930300014c8793030001158893030001b0f4fa021001d9f4fa020001568793030002da8893030002e688930300014c8793030001158893030001b0f4fa021001daf4fa0200013a8793030003da8893030003e688930300014c8793030001158893030001b0f4fa021001dbf4fa020001418793030004da8893030004e688930300014c8793030001158893030001b0f4fa021001dcf4fa02";

 /// <summary>
 /// Day 20 on a seal-only calendar, after a streak broken before day 7.
 /// </summary>
    private const string RetailBrokenBeforeDay7Day20 =
        "ad000000011c141c002001a58793032001da8893032001e688930320014c8793032001158893032001b0f4fa022001dcd355012001568793032002da8893032002e688930320014c8793032001158893032001b0f4fa022001ddd3550120013a8793032003da8893032003e688930320014c8793032001158893032001b0f4fa020001ded355010001418793030004da8893030004e688930300014c8793030001158893030001b0f4fa020001dfd35501";

 /// <summary>
 /// Day 10 after a streak broken after day 7: day 7 keeps its pouch reward, days 14, 21 and 28 are seals.
 /// </summary>
    private const string RetailBrokenAfter7Day10 =
        "ad000000011c0a1c002001a58793032001da8893032001e688930320014c8793032001158893032001b0f4fa023001d9f4fa022001568793032002da8893032002e688930300014c8793030001158893030001b0f4fa020001ddd3550100013a8793030003da8893030003e688930300014c8793030001158893030001b0f4fa020001ded355010001418793030004da8893030004e688930300014c8793030001158893030001b0f4fa020001dfd35501";

 /// <summary>
 /// Day 25: pouches on days 7 and 14, seals on days 21 and 28, streak broken between days 14 and 20.
 /// </summary>
    private const string RetailMenuBrokenAfter14Day25 =
        "ad000000011c191c002001a58793032001da8893032001e688930320014c8793032001158893032001b0f4fa023001d9f4fa022001568793032002da8893032002e688930320014c8793032001158893032001b0f4fa023001daf4fa0220013a8793032003da8893032003e688930320014c8793032001158893032001b0f4fa022001ded355012001418793032004da8893032004e688930320014c8793030001158893030001b0f4fa020001dfd35501";

 /// <summary>
 /// Day 1 on a pouch-only calendar, and day 2 on a seal-only calendar after a missed day.
 /// </summary>
    private const string RetailStreptopeliaDay2AfterAMissedDay =
        "ad000000011c021c002001a58793032001da8893030001e688930300014c8793030001158893030001b0f4fa020001dcd355010001568793030002da8893030002e688930300014c8793030001158893030001b0f4fa020001ddd3550100013a8793030003da8893030003e688930300014c8793030001158893030001b0f4fa020001ded355010001418793030004da8893030004e688930300014c8793030001158893030001b0f4fa020001dfd35501";

    [Theory]
    [InlineData(1, RetailDay1)]
    [InlineData(2, RetailDay2)]
    [InlineData(17, RetailDay17)]
    public void ThePopupOfAnUnbrokenStreakIsRetails(int days, string hex)
        => Assert.Equal(Convert.FromHexString(hex), EconomyMiscPackets.BuildShowAttendanceState(days, 0));

    [Theory]
    [InlineData(20, 1, RetailBrokenBeforeDay7Day20)]
    [InlineData(10, 7, RetailBrokenAfter7Day10)]
    [InlineData(25, 14, RetailMenuBrokenAfter14Day25)]
    [InlineData(2, 1, RetailStreptopeliaDay2AfterAMissedDay)]
    public void AMilestoneAfterTheBreakIsASeal(int days, int brokenAfter, string hex)
        => Assert.Equal(Convert.FromHexString(hex), EconomyMiscPackets.BuildShowAttendanceState(days, brokenAfter));

    [Theory]
    [InlineData(0, 1)]
    [InlineData(27, 28)]
    [InlineData(28, 1)]
    public void TheCountStepsOncePerDayAndWraps(int before, int after)
        => Assert.Equal(after, AttendanceDayGate.NextAttendedDays(before, AttendanceDayGate.CalendarDays));

    [Fact]
    public void AMissedDayBreaksTheStreakUntilTheWrap()
    {
        var jul10 = new DateOnly(2026, 7, 10).DayNumber;
        
        var broken = AttendanceDayGate.NextStreakBrokenAfter(1, jul10, jul10 + 2, 0, next: 2);
        Assert.Equal(1, broken);
        Assert.Equal(Convert.FromHexString(RetailStreptopeliaDay2AfterAMissedDay), EconomyMiscPackets.BuildShowAttendanceState(2, broken));
        
        Assert.Equal(0, AttendanceDayGate.NextStreakBrokenAfter(6, jul10, jul10 + 1, 0, next: 7));
        Assert.Equal(1, AttendanceDayGate.NextStreakBrokenAfter(4, jul10, jul10 + 1, 1, next: 5));
        Assert.Equal(1, AttendanceDayGate.NextStreakBrokenAfter(4, jul10, jul10 + 5, 1, next: 5));
        
        Assert.Equal(0, AttendanceDayGate.NextStreakBrokenAfter(28, jul10 + 7, jul10 + 11, 3, next: 1));
        
        Assert.Equal(0, AttendanceDayGate.NextStreakBrokenAfter(0, 0, jul10, 0, next: 1));
    }

    [Fact]
    public void TheRewardIsTheDaysEntry()
    {
        Assert.Equal((60000486u, (ushort)3), AttendanceDayGate.RewardOf(Convert.FromHexString(RetailDay17), 17));
        Assert.Equal((60000165u, (ushort)1), AttendanceDayGate.RewardOf(Convert.FromHexString(RetailDay1), 1));
        Assert.Equal((50001116u, (ushort)1), AttendanceDayGate.RewardOf(EconomyMiscPackets.BuildShowAttendanceState(28, 0), 28));
        
        Assert.Equal((22402012u, (ushort)1), AttendanceDayGate.RewardOf(EconomyMiscPackets.BuildShowAttendanceState(7, 3), 7));
        Assert.Equal((22402015u, (ushort)1), AttendanceDayGate.RewardOf(EconomyMiscPackets.BuildShowAttendanceState(28, 7), 28));
        Assert.Equal((50001113u, (ushort)1), AttendanceDayGate.RewardOf(EconomyMiscPackets.BuildShowAttendanceState(7, 7), 7));
        Assert.Null(AttendanceDayGate.RewardOf(EconomyMiscPackets.BuildShowAttendanceState(28, 0), 29));
        Assert.Null(AttendanceDayGate.RewardOf(EconomyMiscPackets.BuildShowAttendanceState(28, 0), 0));
    }

    [Fact]
    public void TheMenuViewFloorsAtDayOne()
        => Assert.Equal(Convert.FromHexString(RetailDay1), EconomyMiscPackets.BuildShowAttendanceState(0, 0));
}

/// <summary>3029 AnsMakeOfferings refusals (<see cref="EconomyMiscPackets.BuildMakeOfferingsAck"/>).</summary>
public class MakeOfferingsAck3029Tests
{
 /// <summary>A 1693 targeting a non-NPC handle gets only this 3029 as its answer.</summary>
    [Fact]
    public void ANonNpcTargetGets20055()
        => Assert.Equal(Convert.FromHexString("040000000000574e"),
            EconomyMiscPackets.BuildMakeOfferingsAck(0, EconomyMiscPackets.MakeOfferingsNotUsableNpc));

 /// <summary>Success answer carrying string 15777.</summary>
    [Fact]
    public void ASuccessCarriesTheMessageAndNoError()
        => Assert.Equal(Convert.FromHexString("04000000a13d0000"), EconomyMiscPackets.BuildMakeOfferingsAck(15777, 0));
}

/// <summary>2428 when a held collect quest's item leaves the bag (<see cref="QuestCollectDropTicks"/>).</summary>
public class QuestCollectDropTicksTests
{
    private sealed record QuestRow(uint Quest_ID, byte Complete_Type, uint Complete_Type_Value1, uint Complete_Type_Value2);

    private static readonly JsonSerializerOptions DataJsonOptions =
        new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip };

 /// <summary>Quest_Info.json rows grouped by quest, in file order, as GameDataStore.QuestRows groups them.</summary>
    private static readonly Lazy<IReadOnlyDictionary<uint, QuestData[]>> QuestRows = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "data", "Quest_Info.json")))
            dir = dir.Parent;
        if (dir == null)
            throw new DirectoryNotFoundException("no ancestor of " + AppContext.BaseDirectory + " holds data/Quest_Info.json");
        var rows = JsonSerializer.Deserialize<List<QuestRow>>(
            File.ReadAllBytes(Path.Combine(dir.FullName, "data", "Quest_Info.json")), DataJsonOptions)!;
        return rows.GroupBy(r => r.Quest_ID).ToDictionary(g => g.Key, g => g.Select(r => new QuestData
        {
            Quest_ID = r.Quest_ID,
            Complete_Type = r.Complete_Type,
            Complete_Type_Value1 = r.Complete_Type_Value1,
            Complete_Type_Value2 = r.Complete_Type_Value2,
        }).ToArray());
    });

    private static byte[][] Bodies(uint itemId, IReadOnlyCollection<uint> held, long inBag)
        => QuestCollectDropTicks.PlanTicks(QuestCollectDropTicks.RowsCollecting(QuestRows.Value, itemId), held, inBag)
            .Select(t => QuestItemObjectivePackets.BuildQuestInCompleteNotify2428(t.QuestId, itemId, t.Count))
            .ToArray();

 /// <summary>
 /// A 1693 offer of 18 x 60000775, collected by three quests (50 each), leaves none. The three 2428 notifies
 /// are sent in ascending order before the 2329.
 /// </summary>
    [Fact]
    public void AnOfferingTicksEveryHeldCollectorInQuestOrder()
    {
        var held = new HashSet<uint> { 911000460, 123000024, 911000440, 911000450 };
        Assert.Equal(new[]
        {
            Convert.FromHexString("01121200bde30000000078c34c36078a93030000"),
            Convert.FromHexString("01121200bde30000000082c34c36078a93030000"),
            Convert.FromHexString("01121200bde3000000008cc34c36078a93030000"),
        }, Bodies(60000775, held, 0));
    }

 /// <summary>
 /// Selling one of two copies of an equipment item leaves the capped count unchanged but still sends a 2428 with tick 1.
 /// Selling the second copy sends tick 0.
 /// </summary>
    [Fact]
    public void ASaleTicksEvenWhenTheCappedCountStays()
    {
        var held = new HashSet<uint> { 511000032 };
        Assert.Equal(new[] { Convert.FromHexString("01121200bde300000000e03d751e7efbd7000100") }, Bodies(14154622, held, 1));
        Assert.Equal(new[] { Convert.FromHexString("01121200bde300000000e03d751e7efbd7000000") }, Bodies(14154622, held, 0));
    }

 /// <summary>
 /// A 1337 destroy of the second collect row of 613000010 ticks that row to 0.
 /// </summary>
    [Fact]
    public void ALaterRowIsTickedToo()
    {
        var rows = QuestCollectDropTicks.RowsCollecting(QuestRows.Value, 40001013);
        Assert.Contains(rows, r => r.QuestId == 613000010 && r.RowIndex == 1 && r.Need == 1);
        Assert.Equal(new[] { Convert.FromHexString("01121200bde3000000004aa38924f55d62020000") },
            Bodies(40001013, new HashSet<uint> { 613000010 }, 0));
    }

    [Fact]
    public void NothingIsOwedWhenNoHeldQuestCollectsTheItem()
    {
        Assert.Empty(Bodies(60000775, new HashSet<uint> { 123000024 }, 0));
        Assert.Empty(QuestCollectDropTicks.RowsCollecting(QuestRows.Value, 1));
    }

    [Fact]
    public void TheCountIsCappedAtTheNeed()
    {
        var ticks = QuestCollectDropTicks.PlanTicks(QuestCollectDropTicks.RowsCollecting(QuestRows.Value, 60000775),
            new HashSet<uint> { 911000440 }, 70);
        Assert.Equal(50, Assert.Single(ticks).Count);
    }

    [Fact]
    public void TheFirstRowCounterIsLoweredButNeverRaisedOrCreated()
    {
        var player = new PlayerState();
        player.QuestKillProgress[911000440] = 50;
        QuestCollectDropTicks.LowerFirstRowCounter(player, new QuestCollectDropTicks.Tick(911000440, 0, 0));
        Assert.Equal((byte)0, player.QuestKillProgress[911000440]);

        QuestCollectDropTicks.LowerFirstRowCounter(player, new QuestCollectDropTicks.Tick(911000440, 0, 20));
        Assert.Equal((byte)0, player.QuestKillProgress[911000440]);

        QuestCollectDropTicks.LowerFirstRowCounter(player, new QuestCollectDropTicks.Tick(911000450, 0, 0));
        Assert.False(player.QuestKillProgress.ContainsKey(911000450));

        player.QuestKillProgress[613000010] = 8;
        QuestCollectDropTicks.LowerFirstRowCounter(player, new QuestCollectDropTicks.Tick(613000010, 1, 0));
        Assert.Equal((byte)8, player.QuestKillProgress[613000010]);
    }
}
