namespace RO2.Monster.Domain;

/// <summary>
/// The pure geometry of a monster's chase: how far to step toward its target, and whether it has been dragged past
/// its leash. No host, clock or live state; the host owns the tick, the navmesh cell solve, the 2318 emit and the
/// position writes, and calls these with the numbers.
/// </summary>
public static class ChaseGeometry
{
    /// <summary>A point <paramref name="step"/> units from (fromX, fromY) toward (toX, toY); the origin itself
    /// when they coincide or the distance is non-finite.</summary>
    public static (float X, float Y) StepToward(float fromX, float fromY, float toX, float toY, float step)
    {
        float dx = toX - fromX, dy = toY - fromY;
        float dist = MathF.Sqrt(dx * dx + dy * dy);
        if (dist <= 0f || !float.IsFinite(dist))
            return (fromX, fromY);
        float t = MathF.Min(1f, step / dist);   // never step past the target
        return (fromX + dx * t, fromY + dy * t);
    }

    /// <summary>True when a monster at (posX, posY) is farther than <paramref name="leashBound"/> from its home
    /// (homeX, homeY). A bound of 0 or less (the row carried none) never leashes.</summary>
    public static bool IsPastLeash(float posX, float posY, float homeX, float homeY, float leashBound)
    {
        float homeDx = posX - homeX, homeDy = posY - homeY;
        return leashBound > 0f && MathF.Sqrt(homeDx * homeDx + homeDy * homeDy) > leashBound;
    }

    /// <summary>
    /// The point a chasing monster moves to this pass, or null when it stays put. It closes the gap down to its own
    /// attack reach (<paramref name="attackReach"/> if positive, else <paramref name="fallbackReach"/>), where the
    /// swing takes over: no step when already in reach, when the gap is non-finite, or when the step is not positive.
    /// The step is <c>min(gap - reach, speed * dt)</c>.
    /// </summary>
    public static (float X, float Y)? DecideStep(float posX, float posY, float targetX, float targetY,
        float attackReach, float fallbackReach, float speed, double dt)
    {
        float reach = attackReach > 0f ? attackReach : fallbackReach;
        float dx = targetX - posX, dy = targetY - posY;
        float gap = MathF.Sqrt(dx * dx + dy * dy);
        if (!float.IsFinite(gap) || gap <= reach)
            return null;

        float step = MathF.Min(gap - reach, speed * (float)dt);
        if (step <= 0f)
            return null;

        return StepToward(posX, posY, targetX, targetY, step);
    }
}
