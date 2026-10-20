namespace RO2.Application.Interfaces;

/// <summary>
/// Shared store for the Login→Zone handoff token (see <see cref="RO2.Domain.Entities.ZoneAuthToken"/>).
/// Both servers hit the same MongoDB, so the Login server issues and the Zone server consumes.
/// </summary>
public interface IZoneAuthTokenRepository
{
 /// <summary>Bind a freshly-generated session GUID to the authenticated account for a short window.</summary>
    Task IssueAsync(Guid token, uint accountId, TimeSpan ttl);

 /// <summary>
 /// Validate and CONSUME a token (one-time): returns the bound AccountId if the token exists and has not
 /// expired — deleting it in the process — otherwise null. A null result means the client presented no
 /// proof of a Login-side authentication and must not be trusted.
 /// </summary>
    Task<uint?> ConsumeAsync(Guid token);
}
