namespace RO2.Domain.Entities;

/// <summary>
/// An account's daily attendance state, stored with its gift box. A positional record, so callers can deconstruct it
/// as <c>var (attendedDays, lastDay, brokenAfter)</c>. All three are 0 before the first attended day.
/// </summary>
/// <param name="AttendedDays">Days attended in the current attendance cycle (1..28, the count 3027 carries).</param>
/// <param name="LastAttendedDay">The last attended server day, as <see cref="DateOnly.DayNumber"/> on the server clock.</param>
/// <param name="StreakBrokenAfter">The streak state of the cycle: 0 while every server day since the cycle's day 1 was
/// attended, otherwise the count attended before the first missed day.</param>
public sealed record AccountAttendance(int AttendedDays, int LastAttendedDay, int StreakBrokenAfter);
