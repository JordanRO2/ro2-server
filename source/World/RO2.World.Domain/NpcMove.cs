namespace RO2.Worlds.Domain;

/// <summary>
/// The walk an NPC was last sent on: origin, destination, speed and start time of its 2318 OnMoveNPC.
/// Stored on the host NpcRuntimeState.LastMove so that a 2319 OnNPCViewData sent mid-walk reports the NPC at its
/// current position on that walk rather than at the destination. Foundational World value: the host NPC runtime
/// (and the B6 Monster chase) interpolate position through it.
/// </summary>
/// <remarks>
/// <para>
/// The host NpcMovementService updates the NPC's PosX/PosY to the destination when it sends the 2318. The position
/// reported mid-walk is interpolated continuously: <c>From + (To - From) x min(1, speed x elapsed / distance)</c>,
/// so it stays on the same segment at the same speed.
/// </para>
/// </remarks>
/// <param name="FromX">World X at the start of the walk.</param>
/// <param name="FromY">World Y at the start of the walk.</param>
/// <param name="ToX">World X of the destination.</param>
/// <param name="ToY">World Y of the destination.</param>
/// <param name="StartedUtc">When the 2318 was broadcast.</param>
/// <param name="Speed">Speed in world units per second.</param>
public sealed record NpcMove(float FromX, float FromY, float ToX, float ToY, DateTime StartedUtc, float Speed)
{
 /// <summary>Length of the walk in world units.</summary>
    public float Distance => MathF.Sqrt((ToX - FromX) * (ToX - FromX) + (ToY - FromY) * (ToY - FromY));

 /// <summary>
 /// True while the NPC has not yet covered the walk at <see cref="Speed"/>: <c>speed x elapsed &lt; distance</c>.
 /// A zero-length walk, or a speed that is not a positive finite number, is never in progress.
 /// </summary>
    public bool InProgress(DateTime nowUtc)
    {
        var distance = Distance;
        if (!(distance > 0f) || !float.IsFinite(distance) || !(Speed > 0f) || !float.IsFinite(Speed))
            return false;
        return Speed * ElapsedSeconds(nowUtc) < distance;
    }

 /// <summary>
 /// Position of the NPC at <paramref name="nowUtc"/>: the destination once the walk is covered, and the origin
 /// before it has started.
 /// </summary>
    public (float X, float Y) Position(DateTime nowUtc)
    {
        var distance = Distance;
        if (!(distance > 0f) || !float.IsFinite(distance) || !(Speed > 0f) || !float.IsFinite(Speed))
            return (ToX, ToY);
        var fraction = Math.Min(1f, Speed * ElapsedSeconds(nowUtc) / distance);
        return (FromX + (ToX - FromX) * fraction, FromY + (ToY - FromY) * fraction);
    }

 /// <summary>Seconds since the walk started, never negative.</summary>
    private float ElapsedSeconds(DateTime nowUtc) => (float)Math.Max(0.0, (nowUtc - StartedUtc).TotalSeconds);
}
