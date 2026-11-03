using RO2.Combats.Domain;

namespace RO2.Monster.Domain;

/// <summary>A player as the swing gate sees it: liveness, whether it is on the monster's map, and its position.</summary>
public readonly record struct SwingCandidate(bool IsDead, bool SameMap, float X, float Y);

/// <summary>Which candidate a monster swings at.</summary>
public enum SwingVictim
{
    /// <summary>Nobody qualifies.</summary>
    None,
    /// <summary>The top-threat target.</summary>
    Aggro,
    /// <summary>The player the caller is iterating.</summary>
    Iterating,
}

/// <summary>
/// The pure swing reach gate of a monster: who it may hit and whether that player is within its reach. The host
/// resolves the live handles and the map comparison and calls these with plain numbers.
/// </summary>
public static class SwingReachRules
{
    /// <summary>
    /// Whether (targetX, targetY) is within the monster's horizontal reach from (posX, posY): the monster's own
    /// <paramref name="attackReach"/> (<see cref="MeleeReach.MaxAttackRange"/> when not positive) plus
    /// <see cref="MeleeReach.AttackRangeTolerance"/>, Euclidean 2D, inclusive. A non-finite position is out of reach
    /// (NaN compares false, an infinite distance exceeds any reach).
    /// </summary>
    public static bool InReach(float posX, float posY, float attackReach, float targetX, float targetY)
    {
        float dx = targetX - posX;
        float dy = targetY - posY;
        float reach = (attackReach > 0f ? attackReach : MeleeReach.MaxAttackRange) + MeleeReach.AttackRangeTolerance;
        return MathF.Sqrt(dx * dx + dy * dy) <= reach;
    }

    /// <summary>
    /// The top-threat target when it is alive, on the map and in reach; otherwise the iterating player when it is on
    /// the map and in reach and alive (the iterating player may be the same, already-dead aggro target when a
    /// previous monster killed it earlier in the tick); otherwise none. A null candidate (stale or
    /// unregistered handle) never wins.
    /// </summary>
    public static SwingVictim Select(float posX, float posY, float attackReach,
        SwingCandidate? aggro, SwingCandidate? iterating)
    {
        if (aggro is { } a && !a.IsDead && a.SameMap && InReach(posX, posY, attackReach, a.X, a.Y))
            return SwingVictim.Aggro;
        if (iterating is { } i && !i.IsDead && i.SameMap && InReach(posX, posY, attackReach, i.X, i.Y))
            return SwingVictim.Iterating;
        return SwingVictim.None;
    }
}
