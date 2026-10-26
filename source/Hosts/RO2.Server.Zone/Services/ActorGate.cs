using RO2.GameData;
using RO2.Server.Zone.Models;
using RO2.Worlds.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Host adapter over <see cref="ActorGateRules"/> (RO2.World.Domain): resolves the live PlayerState /
/// NpcRuntimeState into the pure <see cref="GateActor"/> / <see cref="GateTarget"/> records and supplies the
/// data-driven reach, then defers the precondition itself (liveness, same map, in range, ownership) to
/// <see cref="ActorGateRules.CanAct"/>. The pure rule and its result types moved to World.Domain so Combat (its
/// damage/function gates) and Economy (its transaction gate) can both build on it.
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
    /// <summary>The pure precondition, over the primitive records (<see cref="ActorGateRules.CanAct"/>).</summary>
    public static ActorRefusal CanAct(in GateActor actor, in GateTarget? target, in GateContext ctx)
        => ActorGateRules.CanAct(actor, target, ctx);

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
