using RO2.GameData.Tables;
using RO2.Guilds.Domain;

namespace RO2.Guilds.Application;

/// <summary>
/// Credits a member's donated experience through the ledger, then derives the guild level before and after with
/// <see cref="GuildProgression.GuildLevelOf"/>. It sends no wire; the caller sends the level-up notice from the result.
/// </summary>
public sealed class GuildExpCreditUseCase(IGuildExpLedger ledger) : ICreditGuildExp
{
    /// <inheritdoc />
    public async Task<GuildExpCredit> CreditAsync(Guild guild, uint charId, ulong bribeExp, IReadOnlyList<GuildExpTable> expTable)
    {
        var (before, after) = await ledger.CreditMemberAsync(guild, charId, bribeExp);
        return new GuildExpCredit(before, after,
            GuildProgression.GuildLevelOf(before, expTable).Level,
            GuildProgression.GuildLevelOf(after, expTable).Level);
    }
}
