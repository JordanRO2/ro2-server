namespace RO2.Worlds.Domain;

/// <summary>
/// The movement baseline of one player: its map, its last accepted position, and when a client move was last accepted
/// (null before the first one).
/// </summary>
/// <param name="MapId">Map the player is on.</param>
/// <param name="X">Last accepted X position.</param>
/// <param name="Y">Last accepted Y position.</param>
/// <param name="Z">Last accepted Z position.</param>
/// <param name="LastValidatedMoveAtUtc">UTC instant of the last accepted client move, or null when none was accepted yet.</param>
public readonly record struct MoveSubject(uint MapId, float X, float Y, float Z, DateTime? LastValidatedMoveAtUtc);

/// <summary>The outcome of <see cref="MoveGuardRule.Apply"/>: the refusal and the subject state to keep.</summary>
/// <param name="Refusal">The refusal reason, or <see cref="MoveRefusal.None"/> when the move was accepted.</param>
/// <param name="Subject">The state to store: the moved subject when accepted, the unchanged input when refused.</param>
public readonly record struct MoveGuardResult(MoveRefusal Refusal, MoveSubject Subject);

/// <summary>
/// Validates a move against a player's baseline and elapsed time, and returns the new baseline. Takes the current time as
/// a parameter and holds no state.
/// </summary>
/// <remarks>
/// Elapsed time is measured from the last accepted move. A refused move does not advance the baseline, so the only travel
/// a refused player earns is the distance it could have walked in that same time. The first move seen for a player has
/// no baseline and is measured with an elapsed time of 0. A clock that went backwards gives an elapsed time of 0.
/// </remarks>
public static class MoveGuardRule
{
    /// <summary>
    /// Validates <paramref name="dest"/> for <paramref name="subject"/> with <see cref="MovementValidator"/>. On
    /// <see cref="MoveRefusal.None"/> the returned subject is at <paramref name="dest"/> with its baseline set to
    /// <paramref name="nowUtc"/>. On any refusal the returned subject is <paramref name="subject"/> unchanged.
    /// </summary>
    /// <param name="subject">The player's current baseline.</param>
    /// <param name="dest">Claimed new position.</param>
    /// <param name="speed">Actor move speed, world units per second.</param>
    /// <param name="serverGrantedExemption">True only when the server granted the move; see <see cref="MovementValidator.Validate"/>.</param>
    /// <param name="bounds">Map extent, or null when unknown.</param>
    /// <param name="surface">Walkable navmesh, or null when unknown.</param>
    /// <param name="nowUtc">The current UTC instant.</param>
    public static MoveGuardResult Apply(MoveSubject subject, MovePos dest, float speed, bool serverGrantedExemption,
        MoveBounds? bounds, IWalkableSurface? surface, DateTime nowUtc)
    {
        var now = nowUtc;
        var since = subject.LastValidatedMoveAtUtc ?? now;
        var dt = (float)(now - since).TotalSeconds;
        if (dt < 0f) dt = 0f;

        var current = new MovePos(subject.X, subject.Y, subject.Z);
        var refusal = MovementValidator.Validate(subject.MapId, current, dest, dt, speed, bounds, surface, serverGrantedExemption);
        if (refusal != MoveRefusal.None)
            return new MoveGuardResult(refusal, subject);

        return new MoveGuardResult(MoveRefusal.None, subject with { X = dest.X, Y = dest.Y, Z = dest.Z, LastValidatedMoveAtUtc = now });
    }
}
