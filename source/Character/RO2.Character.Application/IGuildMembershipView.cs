namespace RO2.Characters.Application;

/// <summary>
/// A read-only view of guild membership, enough for the character flows that must refuse an action for a guild
/// master. The host backs it with its live guild service.
/// </summary>
public interface IGuildMembershipView
{
    /// <summary>True when the character leads a guild (and so cannot be deleted until the guild is disbanded).</summary>
    Task<bool> IsGuildMasterAsync(uint characterId);
}
