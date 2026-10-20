using System.Buffers.Binary;
using System.Collections.Concurrent;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;

namespace RO2.Server.Zone.Services;

/// <summary>
/// The daily attendance of a login account. On the first login of each server day the attended-day count advances once, that day's reward is filed in the account gift box, and the map-load reply carries the attendance popup. Persisted with the gift box (<see cref="IAccountGiftRepository.TryRecordAttendanceAsync"/>), keyed by account id.
/// </summary>
/// <remarks>
/// <para>
/// The attended-day count advances by one per attended day and wraps from the calendar's last day back to 1. A missed day does not reset it.
/// </para>
/// <para>
/// The streak is tracked separately, because the calendar's milestone rewards depend on whether the cycle was broken.
/// </para>
/// <para>
/// The reward is the calendar entry for the current day, filed as a source-4 gift (<see cref="SourceAttendance"/>).
/// </para>
/// <para>
/// The persisted last attended day gates the popup, so a server restart does not show it or file the reward again on the same day. The in-process cache only avoids re-reading days this process has already recorded.
/// </para>
/// </remarks>
public static class AttendanceDayGate
{
 /// <summary>
 /// Source code of attendance rewards in gift records (2799 record offset 39, 2800 offset 55).
 /// </summary>
    public const byte SourceAttendance = 4;

    
    
    private const int CalendarDaysOffset = 5, EntriesOffset = 9, EntryLength = 6, EntryCountOffset = 1, EntryItemOffset = 2;

 /// <summary>The account's last attended day, as last recorded by this process. A cache of the stored value.</summary>
    private static readonly ConcurrentDictionary<uint, int> KnownAttendedDay = new();

 /// <summary>
 /// Result of <see cref="TryAttendTodayAsync"/>: the new count, the attendance popup body that carries it, and the reward filed with it, or null when the calendar entry names no item.
 /// </summary>
    public sealed record AttendedDay(int AttendedDays, byte[] Popup3027, AccountGift? Reward);

 /// <summary>
 /// Attends the current server day for an account, if it has not been attended yet. Steps the count, files the day's reward in the gift box, and records the day, in one write.
 /// </summary>
 /// <remarks>
 /// Returns null when the day was already attended, and for account 0. A storage error propagates, so the caller sends no popup that cannot be backed by a stored record.
 /// </remarks>
 /// <param name="gifts">Gift repository.</param>
 /// <param name="accountId">Account to attend.</param>
 /// <param name="serverNow">Current server local wall clock time.</param>
 /// <returns>What was recorded, or null when nothing was recorded.</returns>
    public static async Task<AttendedDay?> TryAttendTodayAsync(IAccountGiftRepository gifts, uint accountId, DateTime serverNow,
        CancellationToken ct = default)
    {
        if (accountId == 0) return null;
        int today = DateOnly.FromDateTime(serverNow).DayNumber;
        if (KnownAttendedDay.TryGetValue(accountId, out var known) && known >= today) return null;

        
        
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var (attendedDays, lastDay, brokenAfter) = await gifts.GetAttendanceAsync(accountId, ct);
            if (lastDay >= today)
            {
                KnownAttendedDay[accountId] = lastDay;
                return null;
            }

            int next = NextAttendedDays(attendedDays, CalendarDays);
            int nextBrokenAfter = NextStreakBrokenAfter(attendedDays, lastDay, today, brokenAfter, next);
            
            
            var popup = EconomyMiscPackets.BuildShowAttendanceState(next, nextBrokenAfter);
            var reward = RewardOf(popup, next) is { } r
                ? AccountGiftStore.NewGift(r.ItemId, r.Count, SourceAttendance, DateTime.UtcNow)
                : null;
            if (await gifts.TryRecordAttendanceAsync(accountId, lastDay, today, next, nextBrokenAfter, reward, ct))
            {
                KnownAttendedDay[accountId] = today;
                return new AttendedDay(next, popup, reward);
            }
        }
        return null;
    }

 /// <summary>
 /// The count after one more attended day: 1 for the first day, then plus one, and back to 1 after the calendar's last day.
 /// </summary>
 /// <param name="attendedDays">Count before this day.</param>
 /// <param name="calendarDays">Length of the attendance calendar.</param>
    internal static int NextAttendedDays(int attendedDays, int calendarDays)
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
    internal static int NextStreakBrokenAfter(int attendedDays, int lastDay, int today, int brokenAfter, int next)
    {
        if (next == 1) return 0;
        if (brokenAfter > 0) return brokenAfter;
        return lastDay != 0 && today - lastDay > 1 ? Math.Max(1, attendedDays) : 0;
    }

 /// <summary>
 /// Length of the attendance calendar, which is the cycle the count wraps at. Read from the popup's own day count.
 /// </summary>
    internal static int CalendarDays { get; } = EconomyMiscPackets.BuildShowAttendanceState(1, 0)[CalendarDaysOffset];

 /// <summary>
 /// Gets the item and count of the reward for an attended day, read from an attendance popup body.
 /// </summary>
 /// <param name="popup">Attendance popup body.</param>
 /// <param name="attendedDays">Attended day to look up.</param>
 /// <returns>The item and count, or null when the body has no entry for that day or the entry names no item.</returns>
    internal static (uint ItemId, ushort Count)? RewardOf(ReadOnlySpan<byte> popup, int attendedDays)
    {
        int at = EntriesOffset + EntryLength * (attendedDays - 1);
        if (attendedDays < 1 || popup.Length < at + EntryLength) return null;
        uint itemId = BinaryPrimitives.ReadUInt32LittleEndian(popup[(at + EntryItemOffset)..]);
        byte count = popup[at + EntryCountOffset];
        return itemId == 0 || count == 0 ? null : (itemId, count);
    }
}
