using RO2.BuildingBlocks.Domain;

namespace RO2.Identity.Domain.Events;

/// <summary>Raised when a new account is created by the auto-register branch on first login.</summary>
/// <param name="EventId">Idempotency key, supplied by the producer.</param>
/// <param name="OccurredOn">Instant the registration occurred, supplied from an injected clock.</param>
/// <param name="AccountId">The newly-allocated account id.</param>
/// <param name="Username">The login key the account was registered under.</param>
public sealed record AccountRegistered(
    Guid EventId,
    DateTimeOffset OccurredOn,
    AccountId AccountId,
    string Username) : IDomainEvent;

/// <summary>Raised when credentials verify successfully, including after auto-registration.</summary>
/// <param name="EventId">Idempotency key, supplied by the producer.</param>
/// <param name="OccurredOn">When authentication succeeded.</param>
/// <param name="AccountId">The authenticated account id.</param>
/// <param name="Username">The authenticated login key.</param>
/// <param name="HasSecondPassword">Whether a PIN is set.</param>
/// <param name="WasAutoRegistered">Whether the account was created by this login.</param>
public sealed record AccountAuthenticated(
    Guid EventId,
    DateTimeOffset OccurredOn,
    AccountId AccountId,
    string Username,
    bool HasSecondPassword,
    bool WasAutoRegistered) : IDomainEvent;

/// <summary>Raised when an authentication attempt fails. Used for server-side observability only.</summary>
/// <param name="EventId">Idempotency key, supplied by the producer.</param>
/// <param name="OccurredOn">Instant the attempt failed, supplied from an injected clock.</param>
/// <param name="AccountId">The account the attempt targeted.</param>
/// <param name="Username">The login key the attempt used.</param>
/// <param name="Reason">Why the attempt failed.</param>
public sealed record AuthenticationFailed(
    Guid EventId,
    DateTimeOffset OccurredOn,
    AccountId AccountId,
    string Username,
    AuthenticationFailureReason Reason) : IDomainEvent;

/// <summary>Why an authentication attempt failed.</summary>
public enum AuthenticationFailureReason
{
 /// <summary>The password did not match the stored hash.</summary>
    WrongPassword = 0,

 /// <summary>The password matched, but the account is banned.</summary>
    Banned = 1,
}
