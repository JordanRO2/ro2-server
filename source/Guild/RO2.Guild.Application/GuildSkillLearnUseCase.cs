using RO2.GameData.Tables;
using RO2.Guilds.Domain;

namespace RO2.Guilds.Application;

/// <summary>The outcome of a point-learn: <see cref="Learned"/> is null when refused (then <see cref="Refusal"/> says why).</summary>
public readonly record struct GuildSkillPointLearn(List<uint>? Learned, GuildSkillLearnRefusal Refusal, byte UnspentAfter);

/// <summary>The outcome of a book-learn: the verdict, and the guild's unspent skill points after it.</summary>
public readonly record struct GuildSkillBookLearnResult(GuildSkillBookVerdict Verdict, byte UnspentAfter);

/// <summary>
/// Decides and applies a guild skill learn under the guild's gate, then writes the guild once through the ledger when
/// something was learned. It sends no wire and touches no inventory; the host maps the result to answers and consumes
/// the book.
/// </summary>
public sealed class GuildSkillLearnUseCase(IGuildSkillLedger ledger)
{
    /// <summary>Plans a point-learn of <paramref name="requested"/> and, if accepted, adds every id to the guild's skills and writes the guild.</summary>
    public async Task<GuildSkillPointLearn> LearnAsync(Guild guild, IReadOnlyList<uint> requested, byte guildLevel,
        IReadOnlyDictionary<uint, GuildSkillLevel> table)
    {
        List<uint>? learned;
        GuildSkillLearnRefusal refusal;
        byte unspentAfter;
        lock (guild.Gate)
        {
            learned = GuildSkillRules.PlanLearn(requested, guild.Skills, guildLevel, table, out refusal);
            if (learned is not null)
                foreach (var id in learned)
                    if (!guild.Skills.Contains(id)) guild.Skills.Add(id);
            unspentAfter = GuildSkillRules.UnspentPoints(guildLevel, guild.Skills, table);
        }
        if (learned is not null) await ledger.AddSkillAsync(guild, learned[^1]);
        return new(learned, refusal, unspentAfter);
    }

    /// <summary>Decides what the book skill <paramref name="skillId"/> does and, on <see cref="GuildSkillBookVerdict.Learn"/>, adds it to the guild's skills and writes the guild.</summary>
    public async Task<GuildSkillBookLearnResult> LearnBookAsync(Guild guild, uint skillId, byte guildLevel,
        IReadOnlyDictionary<uint, GuildSkillLevel> table)
    {
        GuildSkillBookVerdict verdict;
        byte unspentAfter;
        lock (guild.Gate)
        {
            verdict = GuildSkillRules.PlanBook(skillId, guild.Skills, guildLevel, table);
            if (verdict == GuildSkillBookVerdict.Learn && !guild.Skills.Contains(skillId))
                guild.Skills.Add(skillId);
            unspentAfter = GuildSkillRules.UnspentPoints(guildLevel, guild.Skills, table);
        }
        if (verdict == GuildSkillBookVerdict.Learn) await ledger.AddSkillAsync(guild, skillId);
        return new(verdict, unspentAfter);
    }
}
