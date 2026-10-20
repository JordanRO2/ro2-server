namespace RO2.Domain.Entities;

/// <summary>
/// One-time handoff token proving that the Login server authenticated an account, so the Zone server
/// can trust the account identity instead of the client packet.
/// </summary>
/// <remarks>
/// Login's SelectServer generates a session GUID and stores it bound to the authenticated account id.
/// The client echoes the GUID in ZoneAuth; the Zone consumes the token (one-time use) and takes the bound
/// account id. Without a valid token the Zone rejects the auth.
/// </remarks>
public class ZoneAuthToken
{
 /// <summary>The session GUID in canonical "D" form; also the document key.</summary>
    public string Id { get; set; } = string.Empty;

 /// <summary>The account the Login server authenticated for this token.</summary>
    public uint AccountId { get; set; }

 /// <summary>UTC instant after which the token is no longer accepted.</summary>
    public DateTime ExpiresAt { get; set; }
}
