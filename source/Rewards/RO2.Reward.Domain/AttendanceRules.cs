namespace RO2.Rewards.Domain;

/// <summary>
/// The pure cycle rules of the daily attendance: the attended-day count wraps at the calendar's last day, and a missed
/// day breaks the streak without resetting the count.
/// </summary>
public static class AttendanceRules
{
 /// <summary>
 /// The count after one more attended day: 1 for the first day, then plus one, and back to 1 after the calendar's last day.
 /// </summary>
 /// <param name="attendedDays">Count before this day.</param>
 /// <param name="calendarDays">Length of the attendance calendar.</param>
    public static int NextAttendedDays(int attendedDays, int calendarDays)
        => attendedDays < 1 || calendarDays < 1 ? 1 : attendedDays % calendarDays + 1;

 /// <summary>
 /// The streak state once a server day is attended.
 /// </summary>
 /// <remarks>
 /// The state is 0 at the start of a cycle. An already broken cycle keeps its stored value. A missed day breaks the cycle and records the count before that day. Because a missed day does not reset the count, this state is stored separately.
 /// </remarks>
 /// <param name="attendedDays">Count before today.</param>
 /// <param name="lastDay">Last attended day.</param>
 /// <param name="today">Day being attended.</param>
 /// <param name="brokenAfter">Stored streak state.</param>
 /// <param name="next">Count after today.</param>
    public static int NextStreakBrokenAfter(int attendedDays, int lastDay, int today, int brokenAfter, int next)
    {
        if (next == 1) return 0;
        if (brokenAfter > 0) return brokenAfter;
        return lastDay != 0 && today - lastDay > 1 ? Math.Max(1, attendedDays) : 0;
    }
}
