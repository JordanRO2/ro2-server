using RO2.Guilds.Domain;

namespace RO2.Guilds.Application;

/// <summary>Online lookup of guild members, behind a port so the guild flows do not reach the player registry.</summary>
public interface IGuildMemberLocator
{
    /// <summary>True when the character is in the world (a player with a map).</summary>
    bool IsOnline(uint charId);

    /// <summary>The session id of the character's live connection, or null when the character is offline.</summary>
    uint? SessionIdOf(uint charId);

    /// <summary>The char ids of <paramref name="guild"/>'s members that are online, in roster order.</summary>
    IReadOnlyList<uint> OnlineMemberCharIds(Guild guild);
}
