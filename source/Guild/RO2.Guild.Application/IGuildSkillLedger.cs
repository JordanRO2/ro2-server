using RO2.Guilds.Domain;

namespace RO2.Guilds.Application;

/// <summary>
/// The guild-side write of a skill learn: the skills are already in the guild's list, and this adds the id if missing
/// and writes the whole guild document once. The live registry implements it in the host.
/// </summary>
public interface IGuildSkillLedger
{
    /// <summary>Adds <paramref name="skillId"/> to the guild's skills if absent, under the guild's gate, and writes the guild.</summary>
    Task AddSkillAsync(Guild guild, uint skillId);
}
