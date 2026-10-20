using RO2.Identity.Domain.Events;

namespace RO2.Identity.Domain.Account;

/// <summary>
/// Outcome of <see cref="Account.Authenticate"/>. On success it carries the state needed for the reply; on failure
/// it carries the <see cref="FailureReason"/>. A success is a normal value, not an exception.
/// </summary>
public sealed class AuthenticationResult
{
    private AuthenticationResult(
        bool succeeded,
        bool wasAutoRegistered,
        bool hasSecondPassword,
        AuthenticationFailureReason? failureReason)
    {
        Succeeded = succeeded;
        WasAutoRegistered = wasAutoRegistered;
        HasSecondPassword = hasSecondPassword;
        FailureReason = failureReason;
    }

 /// <summary>Whether authentication succeeded.</summary>
    public bool Succeeded { get; }

 /// <summary>Whether the account was created by the auto-register branch.</summary>
    public bool WasAutoRegistered { get; }

 /// <summary>Whether a PIN is set.</summary>
    public bool HasSecondPassword { get; }

 /// <summary>The failure reason, or <see langword="null"/> on success.</summary>
    public AuthenticationFailureReason? FailureReason { get; }

 /// <summary>Builds a success result.</summary>
 /// <param name="wasAutoRegistered">True when produced by the auto-register branch.</param>
 /// <param name="hasSecondPassword">Whether a PIN is set.</param>
 /// <returns>A succeeded <see cref="AuthenticationResult"/>.</returns>
    public static AuthenticationResult Success(bool wasAutoRegistered, bool hasSecondPassword)
        => new(true, wasAutoRegistered, hasSecondPassword, null);

 /// <summary>Builds a failure result with the given server-side reason.</summary>
 /// <param name="reason">Why authentication failed (wrong password or active ban).</param>
 /// <returns>A failed <see cref="AuthenticationResult"/>.</returns>
    public static AuthenticationResult Failure(AuthenticationFailureReason reason)
        => new(false, false, false, reason);
}
