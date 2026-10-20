namespace RO2.Application.Interfaces;

/// <summary>
/// Read-only access to an account's GM or staff <c>AccessLevel</c> (0 for a normal player).
/// </summary>
/// <remarks>
/// The level belongs to the account, not to a character, so GM powers follow the account. Zone reads it on world
/// entry and applies it to the in-memory player.
/// </remarks>
public interface IAccountAccessRepository
{
 /// <summary>The account's access level, or 0 when the account cannot be found.</summary>
    Task<byte> GetAccessLevelAsync(uint accountId);
}
