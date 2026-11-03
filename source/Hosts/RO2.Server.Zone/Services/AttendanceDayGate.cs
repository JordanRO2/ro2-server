using System.Collections.Concurrent;
using RO2.Application.Interfaces;
using RO2.Rewards.Application;
using RO2.Rewards.Domain;

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
    public const byte SourceAttendance = AttendTodayUseCase.SourceAttendance;

    
    

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
 /// <param name="attend">The attendance claim use case.</param>
 /// <param name="accountId">Account to attend.</param>
 /// <param name="serverNow">Current server local wall clock time.</param>
 /// <returns>What was recorded, or null when nothing was recorded.</returns>
    public static async Task<AttendedDay?> TryAttendTodayAsync(AttendTodayUseCase attend, uint accountId, DateTime serverNow,
        CancellationToken ct = default)
    {
        if (accountId == 0) return null;
        int today = DateOnly.FromDateTime(serverNow).DayNumber;
        if (KnownAttendedDay.TryGetValue(accountId, out var known) && known >= today) return null;

        
        
        var result = await attend.ExecuteAsync(accountId, today, DateTime.UtcNow, Convert.ToHexString(System.Guid.NewGuid().ToByteArray()), ct);
        if (result is null) return null;
        if (!result.Recorded)
        {
            if (result.State.LastAttendedDay >= today) KnownAttendedDay[accountId] = result.State.LastAttendedDay;
            return null;
        }
        KnownAttendedDay[accountId] = today;
        return new AttendedDay(result.State.AttendedDays,
            EconomyMiscPackets.BuildShowAttendanceState(result.State.AttendedDays, result.State.StreakBrokenAfter), result.Reward);
    }
}
