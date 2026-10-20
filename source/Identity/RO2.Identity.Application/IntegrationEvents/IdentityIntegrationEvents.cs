using RO2.BuildingBlocks.Bus;

namespace RO2.Identity.Application.IntegrationEvents;

/// <summary>
/// Published after an account authenticates successfully. The Login gateway uses it to send the login acknowledgement.
/// Carries semantic fields only; wire encoding is handled by the gateway.
/// </summary>
/// <param name="EventId">Idempotency key.</param>
/// <param name="OccurredOn">When authentication succeeded.</param>
/// <param name="AccountId">The authenticated account id.</param>
/// <param name="Username">The login key.</param>
/// <param name="HasSecondPassword">Whether a PIN is set.</param>
/// <param name="WasAutoRegistered">Whether the account was created by this login.</param>
public sealed record AccountAuthenticatedIntegrationEvent(
    Guid EventId,
    DateTimeOffset OccurredOn,
    uint AccountId,
    string Username,
    bool HasSecondPassword,
    bool WasAutoRegistered) : IIntegrationEvent
{
 /// <inheritdoc />
    public int SchemaVersion => 1;
}

/// <summary>Published when an account is auto-registered on first login.</summary>
/// <param name="EventId">Idempotency key.</param>
/// <param name="OccurredOn">When the account was registered.</param>
/// <param name="AccountId">The new account id.</param>
/// <param name="Username">The registered login key.</param>
public sealed record AccountRegisteredIntegrationEvent(
    Guid EventId,
    DateTimeOffset OccurredOn,
    uint AccountId,
    string Username) : IIntegrationEvent
{
 /// <inheritdoc />
    public int SchemaVersion => 1;
}
