namespace RO2.Worlds.Domain;

/// <summary>Why <see cref="ActorGateRules.CanAct"/> refused an action.</summary>
public enum ActorRefusal
{
    /// <summary>No refusal: the action may proceed.</summary>
    None = 0,
    /// <summary>The actor itself is dead.</summary>
    ActorDead,
    /// <summary>The target handle did not resolve to a live entity record.</summary>
    TargetNotFound,
    /// <summary>The target is dead (and the action needs a living one).</summary>
    TargetDead,
    /// <summary>The target is on another map.</summary>
    CrossMap,
    /// <summary>A position or the reach is not finite, or the horizontal distance exceeds the reach.</summary>
    OutOfRange,
    /// <summary>The caller gave no reach: the action's range is not available as data. Refused, never assumed.</summary>
    RangeUnknown,
    /// <summary>The skill group is not in the actor's learned skills (or is learned at level 0).</summary>
    SkillNotLearned,
    /// <summary>The item the action spends is not owned.</summary>
    ItemNotOwned,
}

/// <summary>Where and how an actor stands: the facts <see cref="ActorGateRules"/> reads from a PlayerState.</summary>
public readonly record struct GateActor(uint MapId, float X, float Y, bool IsDead,
    IReadOnlyDictionary<uint, byte>? LearnedSkills);

/// <summary>The resolved target: a null <see cref="GateTarget"/> passed to the gate means it did not resolve.</summary>
public readonly record struct GateTarget(uint MapId, float X, float Y, bool IsDead);

/// <summary>
/// What the action needs. <see cref="Reach"/> is the full distance allowed (range plus tolerance); null refuses as
/// <see cref="ActorRefusal.RangeUnknown"/>, and an action with no spatial limit passes
/// <see cref="float.PositiveInfinity"/> explicitly. <see cref="SkillGroup"/> / <see cref="ItemOwned"/> are checked
/// only when set.
/// </summary>
public readonly record struct GateContext(float? Reach, uint? SkillGroup = null, bool? ItemOwned = null,
    bool TargetMustBeAlive = true);

/// <summary>
/// Pure shared preconditions for a combat, skill or item action (SP0-ACTORGATE): liveness, same map, in range,
/// ownership. State in, reason out; no DB, no sockets, no clock. Cooldown and rate belong to the cooldown rules and
/// the pending-cast ledger, not here. The host resolves PlayerState / NpcRuntimeState into the primitive
/// <see cref="GateActor"/> / <see cref="GateTarget"/> records and supplies the data-driven reach.
/// </summary>
/// <remarks>
/// Distance is horizontal (X, Y), as the client's own range check measures it. The reach is the caller's: the
/// equipped weapon's auto-attack range for a melee swing, Skill_Range + Influence_Bound_Length + tolerance for a
/// skill, NPCInfo Attack_Range + tolerance for an NPC. The foundational primitive both the Combat gates
/// (EvaluateDamageGate / EvaluateFunctionGate) and the Economy transaction gate build on.
/// </remarks>
public static class ActorGateRules
{
    /// <summary>
    /// The precondition: <see cref="ActorRefusal.None"/> when <paramref name="actor"/> may act on
    /// <paramref name="target"/> under <paramref name="ctx"/>, else the first reason it cannot (liveness, same
    /// map, reach, learned skill, item ownership — in that order).
    /// </summary>
    public static ActorRefusal CanAct(in GateActor actor, in GateTarget? target, in GateContext ctx)
    {
        if (actor.IsDead) return ActorRefusal.ActorDead;
        if (target is not { } t) return ActorRefusal.TargetNotFound;
        if (ctx.TargetMustBeAlive && t.IsDead) return ActorRefusal.TargetDead;
        if (t.MapId != actor.MapId) return ActorRefusal.CrossMap;
        if (ctx.Reach is not { } reach) return ActorRefusal.RangeUnknown;

        var dx = t.X - actor.X;
        var dy = t.Y - actor.Y;
        // Written so that NaN positions or a NaN reach fail the comparison and are refused.
        if (!(MathF.Sqrt(dx * dx + dy * dy) <= reach)) return ActorRefusal.OutOfRange;

        if (ctx.SkillGroup is { } group
            && !(actor.LearnedSkills is { } learned && learned.TryGetValue(group, out var level) && level > 0))
            return ActorRefusal.SkillNotLearned;
        if (ctx.ItemOwned is false) return ActorRefusal.ItemNotOwned;
        return ActorRefusal.None;
    }
}
