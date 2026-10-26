namespace RO2.Combats.Application;

/// <summary>
/// Whether a 1523 ReqSkillMoveNotify / 1525 ReqSkillMoveEnd may move an actor — the caster itself or a monster —
/// given the facts the host resolves (the caster's accepted cast of the skill, the move kind, the skill's reach
/// and the actor's position). Pure: the host does the ledger lookups, the reach-table read and the NPC
/// resolution, then maps the returned reason to its log line.
/// </summary>
public static class SkillMoveAuthorization
{
    /// <summary>Why a move is refused, or <see cref="None"/> when it may proceed.</summary>
    public enum MoveRefusal
    {
        /// <summary>The move is allowed.</summary>
        None,
        /// <summary>The caster has no accepted cast of this skill on this map.</summary>
        NoAcceptedCast,
        /// <summary>The move kind stores a cast target, but the caster's cast did not target itself.</summary>
        SelfNotCastTarget,
        /// <summary>The skill group has no Skill_Table_Base row, so its reach is unknown.</summary>
        NoReachRow,
        /// <summary>The self-move end point is farther than the skill's reach from the caster.</summary>
        SelfOutOfReach,
        /// <summary>The handle is neither the caster nor a tracked live NPC.</summary>
        NotTrackedNpc,
        /// <summary>The actor is not a live monster of the caster's map.</summary>
        NotLiveMonster,
        /// <summary>The actor is not the target of the caster's latest accepted cast of this skill.</summary>
        NotCastTarget,
        /// <summary>The move kind never stores a cast target, so it cannot move an NPC.</summary>
        MoveKindStoresNoTarget,
        /// <summary>The monster stands farther than the skill's reach from the caster.</summary>
        MonsterOutOfReach,
        /// <summary>The reported end point is farther than the skill's reach from the caster.</summary>
        PointOutOfReach,
    }

    /// <summary>The live-monster facts the host resolves for an NPC move; <see cref="Resolved"/> false means no tracked NPC.</summary>
    public readonly record struct MovedNpc(bool Resolved, bool IsDead, bool SameMap, int Camp, float X, float Y);

    /// <summary>Camp of this value or below is a friendly / non-monster NPC that a skill cannot move.</summary>
    private const int FriendlyCampMax = 1;

    /// <summary>True when (bx,by) is within <paramref name="reach"/> of (ax,ay) horizontally (squared compare, NaN-safe via the caller's finite reach).</summary>
    public static bool Within(float ax, float ay, float bx, float by, float reach)
    {
        var dx = bx - ax;
        var dy = by - ay;
        return dx * dx + dy * dy <= reach * reach;
    }

    /// <summary>
    /// Whether the caster may move ITSELF with this skill (a self ground-object move): it needs an accepted cast of
    /// the skill; a move kind that stores a cast target needs that cast to have targeted the caster; the skill must
    /// have a reach; and the end point must be within it.
    /// </summary>
    public static MoveRefusal SelfMove(bool hasCastOfSkill, bool moveKindStoresTarget, bool castTargetedSelf,
        float? reach, float casterX, float casterY, float endX, float endY)
    {
        if (!hasCastOfSkill)
            return MoveRefusal.NoAcceptedCast;
        if (moveKindStoresTarget && !castTargetedSelf)
            return MoveRefusal.SelfNotCastTarget;
        if (reach is not { } r)
            return MoveRefusal.NoReachRow;
        if (!Within(casterX, casterY, endX, endY, r))
            return MoveRefusal.SelfOutOfReach;
        return MoveRefusal.None;
    }

    /// <summary>
    /// Whether the caster may move the monster <paramref name="npc"/> with this skill: a live monster of the
    /// caster's map, the target of the caster's latest accepted cast of this skill, under a move kind that stores a
    /// cast target, with both the monster and the reported end point within the skill's reach of the caster.
    /// </summary>
    public static MoveRefusal NpcMove(MovedNpc npc, bool isCastTarget, bool moveKindStoresTarget, float? reach,
        float casterX, float casterY, float endX, float endY)
    {
        if (!npc.Resolved)
            return MoveRefusal.NotTrackedNpc;
        if (npc.IsDead || !npc.SameMap || npc.Camp <= FriendlyCampMax)
            return MoveRefusal.NotLiveMonster;
        if (!isCastTarget)
            return MoveRefusal.NotCastTarget;
        if (!moveKindStoresTarget)
            return MoveRefusal.MoveKindStoresNoTarget;
        if (reach is not { } r)
            return MoveRefusal.NoReachRow;
        if (!Within(casterX, casterY, npc.X, npc.Y, r))
            return MoveRefusal.MonsterOutOfReach;
        if (!Within(casterX, casterY, endX, endY, r))
            return MoveRefusal.PointOutOfReach;
        return MoveRefusal.None;
    }
}
