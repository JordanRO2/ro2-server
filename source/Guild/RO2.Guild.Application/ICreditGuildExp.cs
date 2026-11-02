using RO2.GameData.Tables;
using RO2.Guilds.Domain;

namespace RO2.Guilds.Application;

/// <summary>
/// What other bounded contexts (Combat, Quest) call to credit a member's donated experience to their guild, without
/// reaching into the guild host. The caller keeps the donation notice before the call and the guild-level-up
/// broadcast after it, sent only when <c>NewLevel &gt; OldLevel</c>.
/// </summary>
public interface ICreditGuildExp
{
    /// <summary>
    /// Credits <paramref name="bribeExp"/> to the guild and to the member's own contribution, written before it
    /// returns. Gives the guild's lifetime experience before and after, and the guild level at each.
    /// </summary>
    Task<GuildExpCredit> CreditAsync(Guild guild, uint charId, ulong bribeExp, IReadOnlyList<GuildExpTable> expTable);
}

/// <summary>The outcome of a donated-experience credit: lifetime experience and guild level, before and after.</summary>
public readonly record struct GuildExpCredit(ulong Before, ulong After, byte OldLevel, byte NewLevel)
{
    /// <summary>True when the credit crossed a guild level.</summary>
    public bool LevelledUp => NewLevel > OldLevel;
}
