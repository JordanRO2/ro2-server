namespace RO2.Combats.Application;

/// <summary>
/// The 1561 ReqSkillStart_Item authorization, after the caster-dead and repeat-window pre-gates (which the host
/// keeps, since they must refuse before the item is even looked up). Decides whether the looked-up item may cast
/// its skill: the caster must own it, it must be in the bag, it must carry exactly the requested skill, and a warp
/// item that moves on this answer must have a destination. Every refusal is answered the same way — the lone 2711,
/// nothing consumed, no cast announced — so the distinction is only the host's log line.
/// </summary>
public static class ItemCastGate
{
    /// <summary>Why a 1561 is refused after the dead/repeat pre-gates, or <see cref="Allowed"/>.</summary>
    public enum Decision
    {
        /// <summary>The item may cast its skill.</summary>
        Allowed,

        /// <summary>The uid did not resolve to an item the caster owns.</summary>
        NotOwned,

        /// <summary>The item is not in the bag (worn, banked, or an extra-bag item), so a consumable cannot be used from it.</summary>
        NotInBag,

        /// <summary>The item does not carry exactly the requested skill (ItemInfo.Skill_ID_1 != skillId).</summary>
        SkillMismatch,

        /// <summary>A warp item that moves on this answer has no destination (no return point / unmovable map).</summary>
        NoWarpDestination,
    }

    /// <summary>
    /// Checks a looked-up 1561, in the host's order: owned → in bag → carries the skill → warp destination.
    /// <paramref name="warpDestinationRequired"/> is true only for a move-on-answer warp item; when it is, a null
    /// destination (<paramref name="warpDestinationResolved"/> false) refuses.
    /// </summary>
    public static Decision Check(bool ownedByCaster, bool inBag, bool skillMatches,
        bool warpDestinationRequired, bool warpDestinationResolved)
    {
        if (!ownedByCaster)
            return Decision.NotOwned;
        if (!inBag)
            return Decision.NotInBag;
        if (!skillMatches)
            return Decision.SkillMismatch;
        if (warpDestinationRequired && !warpDestinationResolved)
            return Decision.NoWarpDestination;
        return Decision.Allowed;
    }
}
