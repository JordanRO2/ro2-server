using RO2.Rewards.Domain;

namespace RO2.Rewards.Application;

/// <summary>
/// Result of <see cref="AttendTodayUseCase"/>.
/// </summary>
/// <param name="State">The attendance as stored after the call: the new state when <paramref name="Recorded"/>, otherwise the last state read.</param>
/// <param name="Recorded">True only for the call that recorded the day.</param>
/// <param name="Reward">The gift filed with the day, or null when the calendar entry names no item or nothing was recorded.</param>
public sealed record AttendTodayResult(AccountAttendance State, bool Recorded, AccountGift? Reward);

/// <summary>
/// Attends a server day for an account, if it has not been attended yet: steps the attended-day count and the streak,
/// picks the calendar reward and records both in one compare-and-swap write on the gift box.
/// </summary>
/// <remarks>
/// The day, the clock and the new gift's id come in as arguments; the use case holds no clock and no cache.
/// A storage error propagates.
/// </remarks>
public sealed class AttendTodayUseCase
{
 /// <summary>Source code of attendance rewards in gift records (2799 record offset 39, 2800 offset 55).</summary>
    public const byte SourceAttendance = 4;

    private const int Attempts = 3;

    private readonly IAccountGiftRepository _gifts;

    /// <summary>Creates the use case over the gift repository.</summary>
    public AttendTodayUseCase(IAccountGiftRepository gifts) => _gifts = gifts;

 /// <param name="accountId">Account to attend.</param>
 /// <param name="today">The server day, as <see cref="DateOnly.DayNumber"/>.</param>
 /// <param name="nowUtc">Filing time of the reward gift.</param>
 /// <param name="giftGuid">Id of the reward gift (32 hex digits); used only when the day's entry names an item.</param>
 /// <param name="ct">Cancellation token.</param>
 /// <returns>
 /// The result, or null when the day was already attended or the write lost the race three times.
 /// </returns>
    public async Task<AttendTodayResult?> ExecuteAsync(uint accountId, int today, DateTime nowUtc, string giftGuid,
        CancellationToken ct = default)
    {
        AccountAttendance? seen = null;
        for (int attempt = 0; attempt < Attempts; attempt++)
        {
            var current = await _gifts.GetAttendanceAsync(accountId, ct);
            if (current.LastAttendedDay >= today) return new AttendTodayResult(current, false, null);
            seen = current;

            int next = AttendanceRules.NextAttendedDays(current.AttendedDays, AttendanceCalendar.Days);
            int nextBrokenAfter = AttendanceRules.NextStreakBrokenAfter(current.AttendedDays, current.LastAttendedDay, today,
                current.StreakBrokenAfter, next);
            var reward = AttendanceCalendar.RewardOf(next, nextBrokenAfter) is { } r
                ? new AccountGift { Guid = giftGuid, ItemId = r.ItemId, Count = r.Count, ReceivedUtc = nowUtc, Source = SourceAttendance }
                : null;
            if (await _gifts.TryRecordAttendanceAsync(accountId, current.LastAttendedDay, today, next, nextBrokenAfter, reward, ct))
                return new AttendTodayResult(new AccountAttendance(next, today, nextBrokenAfter), true, reward);
        }
        return seen is null ? null : new AttendTodayResult(seen, false, null);
    }
}
