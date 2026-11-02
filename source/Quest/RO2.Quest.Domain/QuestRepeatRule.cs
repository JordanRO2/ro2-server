namespace RO2.Quests.Domain;

/// <summary>
/// Pure daily-quest repeat rule: a daily quest can be accepted once per day, the day starting at
/// <see cref="DailyResetUtc"/>. Takes the last completion and the current time as arguments; no clock, no disk.
/// </summary>
public static class QuestRepeatRule
{
    /// <summary>The daily reset time, 13:00 UTC.</summary>
    public static readonly TimeSpan DailyResetUtc = TimeSpan.FromHours(13);

    /// <summary>The most recent daily reset at or before <paramref name="nowUtc"/>.</summary>
    public static DateTime LatestDailyReset(DateTime nowUtc)
    {
        var utc = nowUtc.Kind == DateTimeKind.Local ? nowUtc.ToUniversalTime() : nowUtc;
        var today = new DateTime(utc.Year, utc.Month, utc.Day, 0, 0, 0, DateTimeKind.Utc) + DailyResetUtc;
        return utc >= today ? today : today.AddDays(-1);
    }

    /// <summary>
    /// True when a daily quest last completed at <paramref name="lastCompletedUtc"/> may not be taken again at
    /// <paramref name="nowUtc"/>: it was completed at or after the latest daily reset (a completion at exactly 13:00:00
    /// blocks, one tick earlier does not). Never completed: false.
    /// </summary>
    public static bool DoneToday(DateTime? lastCompletedUtc, DateTime nowUtc)
    {
        if (lastCompletedUtc is not { } done) return false;
        var doneUtc = done.Kind == DateTimeKind.Local ? done.ToUniversalTime() : DateTime.SpecifyKind(done, DateTimeKind.Utc);
        return doneUtc >= LatestDailyReset(nowUtc);
    }
}
