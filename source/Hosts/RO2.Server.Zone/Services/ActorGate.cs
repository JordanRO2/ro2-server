using RO2.Domain.GameData;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>Why <see cref="ActorGate.CanAct"/> refused an action.</summary>
public enum ActorRefusal
{
    None = 0,
    /// <summary>The actor itself is dead.</summary>
    ActorDead,
    /// <summary>The target handle did not resolve to a live entity record.</summary>
    TargetNotFound,
    /// <summary>The target is dead (and the action needs a living one).</summary>
    TargetDead,
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

/// <summary>Where and how an actor stands: the facts <see cref="ActorGate"/> reads from a PlayerState.</summary>
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
/// ownership. State in, reason out; no DB, no sockets, no clock. Cooldown and rate belong to SP0-RATE and the
/// pending-cast ledger (<see cref="SkillCastLandings"/>), not here. Nothing calls it yet.
/// </summary>
/// <remarks>
/// Distance is horizontal (X, Y), as <see cref="AutoAttackState.InRange"/> measures it. The reach is the caller's:
/// <see cref="MeleeReach"/> (equipped weapon Auto_Attack_Range, else the default melee 4, plus the client's own
/// tolerance), <see cref="SkillReach"/> (Skill_Table_Base Skill_Range + Influence_Bound_Length + tolerance) and
/// <see cref="NpcReach"/> (NPCInfo Attack_Range + tolerance) all come from data. Unlike the equip gate there is no
/// invented constant here, but the tolerance is the client's own, not a verified slack for skills (see
/// <see cref="SkillMoveTracker.ReachFor"/>): verify before wiring skills.
/// </remarks>
public static class ActorGate
{
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

    /// <summary>
    /// Precondition of an economy action done at an NPC or another anchor (shop, storage, mail box, auction, cash
    /// mall counter): the player is alive, the <paramref name="anchor"/> resolved, on the same map and within
    /// <paramref name="reach"/> horizontally (NaN-safe). A null anchor is <see cref="ActorRefusal.TargetNotFound"/>.
    /// The anchor may be dead: only the player's own liveness and position are asked.
    /// </summary>
    public static ActorRefusal CanTransact(PlayerState player, NpcRuntimeState? anchor, float reach)
        => CanAct(ActorOf(player), anchor is null ? null : TargetOf(anchor),
            new GateContext(reach, TargetMustBeAlive: false));

    public static GateActor ActorOf(PlayerState p) => new(p.MapId, p.X, p.Y, p.IsDead, p.LearnedSkills);

    public static GateTarget TargetOf(PlayerState p) => new(p.MapId, p.X, p.Y, p.IsDead);

    public static GateTarget TargetOf(NpcRuntimeState n) => new(n.MapId, n.PosX, n.PosY, n.IsDead);

    /// <summary>Basic-attack reach: the weapon's Auto_Attack_Range (0 = unarmed falls back to the melee
    /// <see cref="AutoAttackState.MaxAttackRange"/>) plus <see cref="AutoAttackState.AttackRangeTolerance"/>; the
    /// same figure <see cref="AutoAttackState.InRange"/> uses.</summary>
    public static float MeleeReach(PlayerState p)
        => (p.AutoAttackRange > 0 ? p.AutoAttackRange : AutoAttackState.MaxAttackRange) + AutoAttackState.AttackRangeTolerance;

    /// <summary>Skill reach from Skill_Table_Base for a gated cast (<see cref="SkillMoveTracker.CastReachFor"/>):
    /// Skill_Range + Influence_Bound_Length + tolerance, or <see cref="float.PositiveInfinity"/> when Skill_Range is
    /// 100 (the table's unlimited marker); null when the group has no row.</summary>
    internal static float? SkillReach(uint skillGroup) => SkillMoveTracker.CastReachFor(skillGroup);

    /// <summary>The reach <see cref="CanTransact"/> callers pass for an NPC interaction. The radius is a
    /// loose defensive guard: same-map is the hard rule.</summary>
    public const float NpcInteractReach = 40f;

    /// <summary>An NPC attacker's reach: NPCInfo.Attack_Range plus the same tolerance.</summary>
    public static float NpcReach(NpcData npc) => npc.Attack_Range + AutoAttackState.AttackRangeTolerance;
}
