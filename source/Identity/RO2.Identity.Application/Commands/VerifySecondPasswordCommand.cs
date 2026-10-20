namespace RO2.Identity.Application.Commands;

/// <summary>
/// Verifies the second password (PIN) of an account. If the account has no PIN yet, the submitted PIN is stored instead.
/// </summary>
/// <param name="AccountId">The authenticated account id.</param>
/// <param name="Pin">The PIN, formatted from the payload bytes.</param>
public sealed record VerifySecondPasswordCommand(uint AccountId, string Pin);

/// <summary>Result of <see cref="VerifySecondPasswordCommand"/>.</summary>
/// <param name="AccountFound">Whether the account exists.</param>
/// <param name="HadExistingPin">Whether a PIN was already set (verify) rather than newly stored (set).</param>
/// <param name="WrongPin">Whether a PIN was set and the submitted value did not match.</param>
public sealed record VerifySecondPasswordResult(bool AccountFound, bool HadExistingPin, bool WrongPin);
