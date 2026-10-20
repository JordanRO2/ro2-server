namespace RO2.Application.Interfaces;

/// <summary>
/// Per-account storage of the gift (present) password set with 1600 ReqSetPresentPassword. Per account,
/// not per character. The password itself is never stored, only a salted hash of it.
/// </summary>
public interface IPresentPasswordRepository
{
 /// <summary>True when the account has a gift password set.</summary>
    Task<bool> HasPasswordAsync(uint accountId, CancellationToken ct = default);

 /// <summary>True when the account has a password and <paramref name="password"/> is it.</summary>
    Task<bool> VerifyAsync(uint accountId, string password, CancellationToken ct = default);

 /// <summary>
 /// Set the password. When the account already has one, <paramref name="oldPassword"/> must be it; with
 /// none set there is nothing to protect and the old value is not checked. Returns false and changes
 /// nothing when the old password is wrong or the new one is not a valid 4-digit password.
 /// </summary>
    Task<bool> SetAsync(uint accountId, string oldPassword, string newPassword, CancellationToken ct = default);
}
