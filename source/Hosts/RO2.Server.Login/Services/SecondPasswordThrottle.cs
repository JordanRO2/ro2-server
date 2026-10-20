using RO2.Network.Throttling;

namespace RO2.Server.Login.Services;

/// <summary>
/// Per-account limit on second-password (PIN) submits (RMI 12010).
/// </summary>
/// <remarks>
/// A PIN is a four-digit second factor, so the number of submits per account is bounded server-side.
/// The limit is keyed by account id and survives reconnects. Each processed 12010 for an account spends one
/// unit of its budget (<see cref="MaxAttemptsPerAccount"/> per <see cref="WindowMs"/>). A correct PIN clears
/// the budget (<see cref="Reset"/>). Once the budget is spent, submits are refused without running the
/// verification until the oldest attempt is a full window old. Time is a monotonic-clock millisecond value,
/// so the gate is deterministic under test. It is registered as a singleton.
/// </remarks>
public sealed class SecondPasswordThrottle
{
 /// <summary>PIN submits admitted per account inside one <see cref="WindowMs"/> window.</summary>
    public const int MaxAttemptsPerAccount = 10;

 /// <summary>The window length in milliseconds (10 minutes).</summary>
    public const long WindowMs = 10 * 60 * 1000;

    private readonly SlidingWindowRateLimiter<uint> _perAccount = new(MaxAttemptsPerAccount, WindowMs);

 /// <summary>Number of accounts currently tracked.</summary>
    public int TrackedAccounts => _perAccount.TrackedKeys;

 /// <summary>
 /// Returns <c>true</c> and counts the attempt when <paramref name="accountId"/> may submit another PIN now.
 /// Returns <c>false</c> when the account has used its <see cref="MaxAttemptsPerAccount"/> submits within the
 /// last <see cref="WindowMs"/> milliseconds, and the submit must be refused without verification.
 /// </summary>
    public bool TryAttempt(uint accountId) => _perAccount.TryAcquire(accountId);

 /// <summary>Same as <see cref="TryAttempt(uint)"/>, using an explicit monotonic-clock time.</summary>
    public bool TryAttempt(uint accountId, long nowMs) => _perAccount.TryAcquire(accountId, nowMs);

 /// <summary>Clears the account's budget after a correct PIN, so an accepted login never counts against a later one.</summary>
    public void Reset(uint accountId) => _perAccount.Reset(accountId);
}
