namespace RO2.Identity.Application.Commands;

/// <summary>Sets or replaces the second password (PIN) of an authenticated account.</summary>
/// <param name="AccountId">The authenticated account id.</param>
/// <param name="Pin">The PIN, formatted from the payload bytes.</param>
public sealed record SetSecondPasswordCommand(uint AccountId, string Pin);

/// <summary>
/// Result of <see cref="SetSecondPasswordCommand"/>.
/// </summary>
/// <param name="AccountFound">Whether the account was found and the PIN was stored.</param>
public sealed record SetSecondPasswordResult(bool AccountFound);
