using RO2.Guilds.Domain;

namespace RO2.Guilds.Application;

/// <summary>
/// The guild-side step of an experience credit: takes the guild's gate, applies the pure credit rule
/// (<see cref="GuildProgression.CreditMemberExp"/>) and writes the guild. The live registry implements it in the host.
/// </summary>
public interface IGuildExpLedger
{
    /// <summary>Credits <paramref name="amount"/> to the guild and to member <paramref name="charId"/>; gives the lifetime experience before and after.</summary>
    Task<(ulong Before, ulong After)> CreditMemberAsync(Guild guild, uint charId, ulong amount);
}
