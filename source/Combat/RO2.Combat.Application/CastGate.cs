using RO2.Combats.Domain;

namespace RO2.Combats.Application;

/// <summary>
/// The 1508 ReqSkillStart precondition, before any pending cast, SP spend, cooldown arm or RecentSkillStart write:
/// why a cast start is refused, or that it may proceed. Every refusal is answered the same way on the wire — one
/// silent 2715 carrying 20152, echoing the request's target/vec/variant, and nothing else — so the distinction is
/// only for the host's log line.
/// </summary>
public static class CastGate
{
    /// <summary>Why a 1508 cast start is refused, or <see cref="Allowed"/>.</summary>
    public enum Decision
    {
        /// <summary>The cast may start.</summary>
        Allowed,

        /// <summary>An item skill, which starts only through RMI 1561, never a raw 1508.</summary>
        ItemSkill,

        /// <summary>The caster is dead; a dead caster casts nothing.</summary>
        Dead,

        /// <summary>A talent class skill the caster has not learned (or that has no table row for the exact id).</summary>
        NotLearnedTalent,
    }

    /// <summary>
    /// Checks a 1508 of <paramref name="skillId"/>. An item skill is refused first (<see cref="Decision.ItemSkill"/>).
    /// A dead caster is next (<see cref="Decision.Dead"/>). Then a TALENT class skill (below
    /// <see cref="SkillActionGate.FirstNonClassSkillId"/> with a <see cref="ISkillCatalog.HasTalentLimit"/>) must be
    /// learned — have a level row for the exact id AND <paramref name="learnedLevelOfGroup"/> above 0 — else
    /// <see cref="Decision.NotLearnedTalent"/>. Non-talent, guild, item and monster skills are exempt from the learned
    /// check (never in LearnedSkills; item ones are vouched for by the 1561 path). Otherwise <see cref="Decision.Allowed"/>.
    /// </summary>
    public static Decision Check(ISkillCatalog catalog, uint skillId, bool casterIsDead, byte learnedLevelOfGroup)
    {
        if (catalog.IsItemSkill(skillId))
            return Decision.ItemSkill;
        if (casterIsDead)
            return Decision.Dead;

        var skillGroup = skillId / SkillActionGate.SkillGroupDivisor;
        var notLearnedTalent = skillId < SkillActionGate.FirstNonClassSkillId
            && catalog.HasTalentLimit(skillGroup)
            && (!catalog.HasLevelRow(skillId) || learnedLevelOfGroup == 0);
        return notLearnedTalent ? Decision.NotLearnedTalent : Decision.Allowed;
    }
}
