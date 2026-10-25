using RO2.GameData;
using RO2.GameData.Tables;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Calendar and request decoding for the ranking boards: which Ranking_Reset row a ranking type follows, when its
/// period starts and ends, and what the filter byte of a 1654/1655 request selects. The functions are pure, so the
/// store and the tests share one definition.
/// </summary>
/// <remarks>
/// A type with a Ranking.json row follows that row's Reset_Type. Type 1 has no row and is daily. A period starts at the
/// reset time of day (<see cref="ResetTimeOfDayUtc"/>): daily periods start every day, weekly periods start on the
/// weekdays named by Reset_Week (Sunday first), and monthly periods start on Reset_Day.
/// The filter byte is <c>(selection &lt;&lt; 2) | Filter_Type</c>. Filter_Type 1 selects a combat class and 2 selects a
/// creation profession. Selection 0 means all.
/// </remarks>
public static class RankingSchedule
{
    public const byte ResetDaily = 1;
    public const byte ResetWeekly = 2;
    public const byte ResetMonthly = 3;

 /// <summary>Ranking type 1, "Wanderer": Kafra transports. It has no Ranking.json row.</summary>
    public const byte TypeKafraTransport = 1;

 /// <summary>Ranking type 3, "Hunter": the day's kills of monsters that give EXP.</summary>
    public const byte TypeMonsterHunt = 3;

 /// <summary>Ranking type 4, "Collector": the day's card pickups that open a new bag row.</summary>
    public const byte TypeCardCollector = 4;

 /// <summary>Filter_Type 1: the combo lists combat classes (JobInfo ids).</summary>
    public const byte FilterByClass = 1;

 /// <summary>Filter_Type 2: the combo lists creation professions (ProJob_Type ids).</summary>
    public const byte FilterByProfession = 2;

 /// <summary>The reset instant as a UTC time of day: Public_Time 5 (see the remarks).</summary>
    public static TimeSpan ResetTimeOfDayUtc => PlayTimeRewardChain.ResetTimeOfDayUtc;

 /// <summary>
 /// The Reset_Type of <paramref name="rankingType"/>: its Ranking.json row, daily for type 1, or 0 (no schedule) for a
 /// type the client never asks for.
 /// </summary>
    public static byte ResetTypeOf(IReadOnlyList<Ranking> table, byte rankingType)
    {
        foreach (var row in table)
            if (row.Ranking_Type == rankingType)
                return row.Reset_Type;
        return rankingType == TypeKafraTransport ? ResetDaily : (byte)0;
    }

 /// <summary>The Ranking_Reset row that governs <paramref name="rankingType"/>, or null.</summary>
    public static RankingReset? ResetRowOf(GameDataStore store, byte rankingType)
    {
        byte resetType = ResetTypeOf(store.Ranking, rankingType);
        foreach (var row in store.RankingReset)
            if (row.Reset_Type == resetType)
                return row;
        return null;
    }

 /// <summary>The start (UTC) of the period of <paramref name="reset"/> that holds <paramref name="nowUtc"/>:
 /// the most recent reset instant at or before it.</summary>
    public static DateTime PeriodStart(RankingReset reset, DateTime nowUtc)
    {
        var at = DateTime.SpecifyKind(nowUtc.Date, DateTimeKind.Utc) + ResetTimeOfDayUtc;
        if (at > nowUtc) at = at.AddDays(-1);
        switch (reset.Reset_Type)
        {
            case ResetWeekly:
                
                if (WeekFlags(reset.Reset_Week) is { } flags && flags.Any(f => f))
                    while (!flags[(int)at.DayOfWeek]) at = at.AddDays(-1);
                return at;
            case ResetMonthly:
                {
                    int day = Math.Max(1, (int)reset.Reset_Day);
                    var candidate = MonthlyInstant(at.Year, at.Month, day);
                    return candidate <= nowUtc ? candidate : MonthlyInstant(at.AddMonths(-1).Year, at.AddMonths(-1).Month, day);
                }
            default:
                return at;
        }
    }

 /// <summary>The start of the period before the one starting at <paramref name="periodStartUtc"/>.</summary>
    public static DateTime PreviousPeriodStart(RankingReset reset, DateTime periodStartUtc)
        => PeriodStart(reset, periodStartUtc.AddTicks(-1));

 /// <summary>The first reset instant strictly after <paramref name="nowUtc"/>.</summary>
    public static DateTime NextResetAfter(RankingReset reset, DateTime nowUtc)
    {
        
        
        var current = PeriodStart(reset, nowUtc);
        for (var probe = current.AddDays(1); ; probe = probe.AddDays(1))
            if (PeriodStart(reset, probe) > current)
                return PeriodStart(reset, probe);
    }

 /// <summary>Splits a 1654/1655 filter byte into the combo selection (0 = all) and the tab's Filter_Type.</summary>
    public static (byte Selection, byte FilterType) DecodeFilter(byte filter) => ((byte)(filter >> 2), (byte)(filter & 3));

 /// <summary>Reset_Week as seven Sunday-first flags, or null when it is not seven fields.</summary>
    private static bool[]? WeekFlags(string week)
    {
        var parts = week.Split(':');
        if (parts.Length != 7) return null;
        return parts.Select(p => p.Trim() == "1").ToArray();
    }

    private static DateTime MonthlyInstant(int year, int month, int day)
        => new DateTime(year, month, Math.Min(day, DateTime.DaysInMonth(year, month)), 0, 0, 0, DateTimeKind.Utc)
           + ResetTimeOfDayUtc;
}
