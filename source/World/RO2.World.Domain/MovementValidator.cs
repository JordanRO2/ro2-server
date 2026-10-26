namespace RO2.Worlds.Domain;

/// <summary>A position in world units.</summary>
public readonly record struct MovePos(float X, float Y, float Z);

/// <summary>Reason <see cref="MovementValidator.Validate"/> refused a step.</summary>
public enum MoveRefusal
{
    /// <summary>The step is accepted.</summary>
    None = 0,
 /// <summary>A coordinate, elapsed time or speed is not a finite number, or the time or speed is negative.</summary>
    NonFinite,
 /// <summary>The destination lies outside the map extent.</summary>
    OutOfBounds,
 /// <summary>The step is longer than the speed allows for the elapsed time.</summary>
    TooFast,
 /// <summary>The destination is not on the walkable surface.</summary>
    NotWalkable,
}

/// <summary>Axis-aligned map extent in world units, inclusive. X and Y are the ground plane.</summary>
public readonly record struct MoveBounds(float MinX, float MaxX, float MinY, float MaxY)
{
    /// <summary>True when <paramref name="p"/> lies within this extent on the X/Y ground plane (inclusive).</summary>
    public bool Contains(MovePos p) => p.X >= MinX && p.X <= MaxX && p.Y >= MinY && p.Y <= MaxY;
}

/// <summary>Answers whether a world position is walkable. Returns null when the map has no data.</summary>
public interface IWalkableSurface
{
    /// <summary>True/false when <paramref name="mapId"/> has navmesh data, null when it has none.</summary>
    bool? IsWalkable(uint mapId, MovePos pos);
}

/// <summary>
/// Checks a movement step for plausibility: finite coordinates, optional map extent, step length against the allowed
/// distance for the elapsed time, and optional walkability.
/// </summary>
/// <remarks>
/// Distance is measured on the XY ground plane. Z is only checked for finiteness. The allowed step length is
/// <c>speed * (dt + 0.5 s)</c>, where the speed is the actor's current speed including buffs and mounts. A server-granted
/// move (dash or skill) is exempt from the length check. Position changes the server makes itself do not go through this
/// validator. Walkability and map extent are skipped when no data is supplied.
/// </remarks>
public static class MovementValidator
{
 /// <summary>Multiplier applied to speed times elapsed time. Fixed at 1.0.</summary>
    public const float Slack = 1.0f;

 /// <summary>Additive allowance in seconds added to the allowed travel time.</summary>
    public const float ToleranceSeconds = 0.5f;

 /// <summary>Extra allowed distance, in world units, for the given speed.</summary>
    public static double FixedTolerance(float speed) => (double)speed * ToleranceSeconds;

 /// <summary>Maximum allowed step length, in world units, for the given speed and elapsed time.</summary>
    public static double MaxStep(float speed, float dt) => (double)speed * dt * Slack + FixedTolerance(speed);

 /// <summary>Validates a claimed movement step; returns <see cref="MoveRefusal.None"/> when it is plausible.</summary>
 /// <param name="mapId">Map the destination is on (only used for the walkable lookup).</param>
 /// <param name="current">Last accepted position.</param>
 /// <param name="destination">Claimed new position.</param>
 /// <param name="dt">Seconds since <paramref name="current"/> was accepted.</param>
 /// <param name="speed">Actor move speed, world units per second.</param>
 /// <param name="bounds">Map extent, or null when unknown.</param>
 /// <param name="surface">Walkable navmesh, or null when unknown.</param>
 /// <param name="serverGrantedExemption">True only when the SERVER granted this move (dash, skill move) and it may
 /// legitimately exceed the speed cap; skips the step-length check only (finiteness, bounds and walkability still
 /// apply). Never derive it from the client's request. Warp / knockback / respawn must not reach this validator.</param>
    public static MoveRefusal Validate(uint mapId, MovePos current, MovePos destination, float dt, float speed,
        MoveBounds? bounds = null, IWalkableSurface? surface = null, bool serverGrantedExemption = false)
    {
        if (!IsFinite(current) || !IsFinite(destination)
            || !float.IsFinite(dt) || !float.IsFinite(speed) || dt < 0f || speed < 0f)
            return MoveRefusal.NonFinite;

        if (bounds is { } b && !b.Contains(destination))
            return MoveRefusal.OutOfBounds;

        double dx = (double)destination.X - current.X;
        double dy = (double)destination.Y - current.Y;

        double len = Math.Sqrt(dx * dx + dy * dy);
        if (!serverGrantedExemption && len > MaxStep(speed, dt))
            return MoveRefusal.TooFast;

        if (surface?.IsWalkable(mapId, destination) == false)
            return MoveRefusal.NotWalkable;

        return MoveRefusal.None;
    }

    private static bool IsFinite(MovePos p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
}
