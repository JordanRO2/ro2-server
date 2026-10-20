namespace RO2.Identity.Application.Commands;

/// <summary>Authenticates a username and password. An unknown username is registered on first use.</summary>
/// <param name="Username">The login key.</param>
/// <param name="Password">The plaintext password.</param>
public sealed record AuthenticateCommand(string Username, string Password);

/// <summary>Outcome of <see cref="AuthenticateCommand"/>, used by the Login gateway to build the reply.</summary>
/// <param name="Succeeded">Whether authentication succeeded.</param>
/// <param name="AccountId">The account id on success; <c>0</c> on failure.</param>
/// <param name="Username">The login key.</param>
/// <param name="HasSecondPassword">Whether a second password (PIN) is set.</param>
/// <param name="WasAutoRegistered">Whether this call created the account. The reply is the same either way.</param>
/// <param name="Failure">The failure reason; <see cref="AuthenticateFailure.None"/> on success.</param>
public sealed record AuthenticateResult(
    bool Succeeded,
    uint AccountId,
    string Username,
    bool HasSecondPassword,
    bool WasAutoRegistered,
    AuthenticateFailure Failure = AuthenticateFailure.None);

/// <summary>Failure reasons for <see cref="AuthenticateCommand"/>.</summary>
public enum AuthenticateFailure
{
 /// <summary>No failure.</summary>
    None = 0,

 /// <summary>The password does not match the account (reply code 1004).</summary>
    WrongPassword = 1,

 /// <summary>The account is banned (reply code 1005).</summary>
    Banned = 2,

 /// <summary>The account does not exist and the name cannot be registered (reply code 1003).</summary>
    UnknownAccount = 3,
}
