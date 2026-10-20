namespace RO2.Identity.Application.Commands;

/// <summary>
/// Command to register a new account. Login normally registers accounts implicitly through
/// <see cref="AuthenticateCommand"/>; this command is the explicit path.
/// </summary>
/// <param name="Username">The desired login key.</param>
/// <param name="Password">The plaintext password.</param>
public sealed record RegisterAccountCommand(string Username, string Password);

/// <summary>Result of <see cref="RegisterAccountCommand"/>.</summary>
/// <param name="AccountId">The new account id.</param>
/// <param name="Username">The registered login key.</param>
public sealed record RegisterAccountResult(uint AccountId, string Username);
