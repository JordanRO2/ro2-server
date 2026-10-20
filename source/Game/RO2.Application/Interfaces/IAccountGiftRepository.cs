using RO2.Domain.Entities;

namespace RO2.Application.Interfaces;

/// <summary>
/// Persistence for the account gift box and the account's daily attendance. One box is shared by the characters
/// of a login account.
/// </summary>
/// <remarks>
/// Attendance is stored in the box document, so recording an attended day and filing its calendar reward happen
/// in one write. A box written before attendance existed reads as never attended. Wire formatting and paging stay
/// in the Zone host; the shared expiry rules are in <see cref="AccountGiftRules"/>.
/// </remarks>
public interface IAccountGiftRepository
{
 /// <summary>Returns the account's live gifts, newest first. Expired gifts are removed from storage.</summary>
    Task<IReadOnlyList<AccountGift>> GetLiveAsync(uint accountId, DateTime nowLocal, CancellationToken ct = default);

 /// <summary>
 /// Adds <paramref name="gifts"/> to the account's box in one atomic update, creating the box if needed.
 /// </summary>
 /// <returns>The number of live gifts in the box after the update.</returns>
    Task<int> FileAsync(uint accountId, IReadOnlyList<AccountGift> gifts, DateTime nowLocal, CancellationToken ct = default);

 /// <summary>
 /// Removes the gift <paramref name="guid"/> from the account's box. Returns true only for the call that removed it.
 /// </summary>
 /// <remarks>
 /// Known limitation: the update also sets the box's UpdatedAt, so it reports a modification whenever the account
 /// has a box, even when the gift was not present.
 /// </remarks>
    Task<bool> TryTakeAsync(uint accountId, string guid, CancellationToken ct = default);

 /// <summary>
 /// Returns the account's attendance state: days attended in the cycle, last attended server day and streak state.
 /// All zero before the first attendance.
 /// </summary>
    Task<AccountAttendance> GetAttendanceAsync(uint accountId, CancellationToken ct = default);

 /// <summary>
 /// Records <paramref name="today"/> as attended and, in the same write, files <paramref name="reward"/> when it is
 /// not null. The write applies only while the stored last attended day still equals
 /// <paramref name="lastAttendedDaySeen"/>, the value the caller read.
 /// </summary>
 /// <returns>True for the one call that recorded the day; false when another load recorded it first.</returns>
    Task<bool> TryRecordAttendanceAsync(uint accountId, int lastAttendedDaySeen, int today, int attendedDays,
        int streakBrokenAfter, AccountGift? reward, CancellationToken ct = default);
}
