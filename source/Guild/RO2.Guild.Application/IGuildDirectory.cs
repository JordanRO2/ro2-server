using RO2.Guilds.Domain;

namespace RO2.Guilds.Application;

/// <summary>
/// Read model of the guild registry for the flows that only look guilds up (chat audience, roster), behind a port so
/// they do not reach the live registry singleton.
/// </summary>
public interface IGuildDirectory
{
    /// <summary>The char ids of every member (online or not) of the guild, or empty when it does not exist.</summary>
    IReadOnlyList<uint> GetGuildMemberCharIds(uint guildId);

    /// <summary>The guild the character belongs to, or null.</summary>
    Guild? GetByCharId(uint charId);
}
