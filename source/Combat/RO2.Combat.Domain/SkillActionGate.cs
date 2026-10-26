using RO2.Worlds.Domain;

namespace RO2.Combats.Domain;

/// <summary>
/// The 1509 pre-effect gates: the pure preconditions a landed skill must pass before it damages (the attack
/// path) or takes effect (revival / emergency call / warp items). Both build on the shared
/// <see cref="ActorGateRules.CanAct"/> (World.Domain): the host resolves the target and reach and passes the
/// learned-skill map in, so these are unit-testable without live state or the shipped tables.
/// </summary>
public static class SkillActionGate
{
    /// <summary>First skill id that is not a class skill: the 2,000,000+ ids are item, guild and monster skills, which
    /// are never in a PlayerState's LearnedSkills (SkillStartHandler uses the same boundary).</summary>
    public const uint FirstNonClassSkillId = 2_000_000u;

    /// <summary>Skill ids are <c>group * SkillGroupDivisor + level</c>: id / 100 is the skill group, id % 100 the cast level.</summary>
    public const uint SkillGroupDivisor = 100u;

    /// <summary>
    /// The 1509 pre-damage precondition: when the cast names an NPC primary target
    /// (<paramref name="npcTarget"/> non-null), it must pass <see cref="ActorGateRules.CanAct"/>: alive, same map,
    /// within <paramref name="reach"/> (<see cref="float.PositiveInfinity"/> for a Skill_Range-100 skill, so the
    /// range check is skipped) and the skill learned. A ground / self AoE names no NPC primary
    /// (<paramref name="npcTarget"/> null), and only the learned-skill check applies there (a PC primary is left to
    /// the field-duel path, which gates PC damage itself). A class skill's cast level (id % 100) must also lie in
    /// [1, the learned level of its group] and not above <paramref name="maxSkillLevel"/> (the group's
    /// Talent_Table.Max_Skill_Level, null when it has none): the level is the client's, and CombatService prices the
    /// hit off it. Returns <see cref="ActorRefusal.None"/> when the cast may proceed.
    /// </summary>
    public static ActorRefusal EvaluateDamageGate(in GateActor actor, uint skillId, in GateTarget? npcTarget,
        float? reach, byte? maxSkillLevel = null)
    {
        // Item skills are validated by POSSESSION, not by a learned check. An item skill
        // (>= FirstNonClassSkillId) is never in LearnedSkills, so a learned check would refuse a legitimate damaging
        // item skill (item held, cast through the 1561 path that already verified ownership and consumed the item,
        // leaving the pending cast that TryGovern below matches). Skip the learned check for it, exactly as
        // EvaluateFunctionGate does; only a class skill is learned-gated. The possession proof is the pending cast
        // the 1561 path recorded: a forged item-skill 1509 with no such cast finds none at TryGovern and is refused.
        uint? learnedGroup = skillId < FirstNonClassSkillId ? skillId / SkillGroupDivisor : null;
        var refusal = npcTarget is { } target
            ? ActorGateRules.CanAct(actor, target, new GateContext(reach, SkillGroup: learnedGroup))
            : learnedGroup is not null
                ? (actor.LearnedSkills is { } known && known.TryGetValue(learnedGroup.Value, out var knownLevel) && knownLevel > 0
                    ? ActorRefusal.None
                    : ActorRefusal.SkillNotLearned)
                : ActorRefusal.None;
        if (refusal != ActorRefusal.None || learnedGroup is not { } group)
            return refusal;

        // The cast level is the id's own (id % 100) and the client's to choose, so it must lie in
        // [1, learned level of the group] and within the group's Max_Skill_Level when the caller has one
        // (TalentRules.TryGetSkillLevelLimit). Learned >= 1 is established above, so the lookup holds.
        var castLevel = skillId % SkillGroupDivisor;
        var learnedLevel = actor.LearnedSkills![group];
        return castLevel >= 1 && castLevel <= learnedLevel && (maxSkillLevel is not { } max || castLevel <= max)
            ? ActorRefusal.None
            : ActorRefusal.SkillNotLearned;
    }

    /// <summary>
    /// The pre-effect precondition of the non-damage 1509 functions (revival, emergency call, the warp
    /// items): the actor must be alive; a <paramref name="targeted"/> cast needs its resolved target on the actor's
    /// map within the skill's <paramref name="reach"/> (alive only when <paramref name="targetMustBeAlive"/>, which
    /// a revival turns off since it raises a dead PC); and a class skill (below <see cref="FirstNonClassSkillId"/>)
    /// must be learned. An item or guild skill is vouched for elsewhere (the 1561's item check, the guild
    /// validation), so it skips the learned check. Pure, like <see cref="EvaluateDamageGate"/>.
    /// </summary>
    public static ActorRefusal EvaluateFunctionGate(in GateActor actor, uint skillId, in GateTarget? target,
        bool targeted, float? reach, bool targetMustBeAlive)
    {
        uint? learnedGroup = skillId < FirstNonClassSkillId ? skillId / SkillGroupDivisor : null;
        if (targeted)
            return ActorGateRules.CanAct(actor, target,
                new GateContext(reach, SkillGroup: learnedGroup, TargetMustBeAlive: targetMustBeAlive));
        if (actor.IsDead) return ActorRefusal.ActorDead;
        return learnedGroup is { } group
            && !(actor.LearnedSkills is { } learned && learned.TryGetValue(group, out var level) && level > 0)
            ? ActorRefusal.SkillNotLearned
            : ActorRefusal.None;
    }
}
