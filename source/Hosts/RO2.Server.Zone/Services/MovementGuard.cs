using RO2.GameData;
using RO2.Worlds.Domain;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>Current UTC instant, provided through an interface so tests can control elapsed time.</summary>
public interface IClock
{
 /// <summary>The current UTC instant.</summary>
    DateTime UtcNow { get; }
}

/// <summary>System clock implementation of <see cref="IClock"/>.</summary>
public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}

/// <summary>
/// Server-side check of client-reported player positions. A position is applied only when it passes
/// <see cref="MovementValidator"/> against the last accepted position and the elapsed time. A refused position is not
/// stored and is not rebroadcast, so the player keeps its last valid position.
/// </summary>
/// <remarks>
/// The accepted position is the player's current position. Server-initiated position changes (warp, glider, knockback,
/// respawn) set that position directly and do not pass through this check. A refused move does not advance the time
/// baseline. The first move seen for a player sets the baseline at its current position.
/// </remarks>
public sealed class MovementGuard
{
    private readonly IClock _clock;

    private readonly float _maxElapsedSeconds;

 /// <param name="clock">Time source.</param>
 /// <param name="maxElapsedSeconds">Cap on the elapsed time one move may claim (<c>Game.MoveMaxElapsedSeconds</c>); must be positive and finite.</param>
    public MovementGuard(IClock clock, float maxElapsedSeconds = MovementValidator.DefaultMaxElapsedSeconds)
    {
        if (!float.IsFinite(maxElapsedSeconds) || maxElapsedSeconds <= 0f)
            throw new ArgumentOutOfRangeException(nameof(maxElapsedSeconds), maxElapsedSeconds, "must be positive and finite");
        _clock = clock;
        _maxElapsedSeconds = maxElapsedSeconds;
    }

 /// <summary>
 /// Validates a client-driven move against the player's last accepted position and the elapsed time. On success the player
 /// moves to <paramref name="dest"/>. On refusal nothing changes.
 /// </summary>
    public MoveRefusal ValidateClientMove(PlayerState player, MovePos dest, float speed,
        MoveBounds? bounds = null, IWalkableSurface? surface = null)
        => Apply(player, dest, speed, serverGrantedExemption: false, bounds, surface);

 /// <summary>
 /// Applies a move the server itself granted, such as a dash or skill move. The step-length check is skipped, the coordinate
 /// and bounds checks still apply, and the baseline is refreshed.
 /// </summary>
    public MoveRefusal ApplyServerGranted(PlayerState player, MovePos dest,
        MoveBounds? bounds = null, IWalkableSurface? surface = null)
        => Apply(player, dest, speed: 0f, serverGrantedExemption: true, bounds, surface);

 /// <summary>World units added on every side of a map's navmesh extent, so only a wildly out-of-map coordinate is refused.</summary>
    public const float BoundsPadding = 1000f;

 /// <summary>The map's navmesh extent padded by <see cref="BoundsPadding"/>, or null (accept) when the map has no navmesh.</summary>
    public static MoveBounds? BoundsFor(NavMesh? mesh)
    {
        if (mesh is null) return null;
        var e = mesh.WorldExtent;
        return new MoveBounds((float)e.MinX - BoundsPadding, (float)e.MaxX + BoundsPadding,
                              (float)e.MinY - BoundsPadding, (float)e.MaxY + BoundsPadding);
    }

    private MoveRefusal Apply(PlayerState player, MovePos dest, float speed, bool serverGrantedExemption,
        MoveBounds? bounds, IWalkableSurface? surface)
    {
        var subject = new MoveSubject(player.MapId, player.X, player.Y, player.Z, player.LastValidatedMoveAtUtc);
        var result = MoveGuardRule.Apply(subject, dest, speed, serverGrantedExemption, bounds, surface, _clock.UtcNow, _maxElapsedSeconds);
        if (result.Refusal != MoveRefusal.None)
            return result.Refusal;

        player.X = result.Subject.X;
        player.Y = result.Subject.Y;
        player.Z = result.Subject.Z;
        player.LastValidatedMoveAtUtc = result.Subject.LastValidatedMoveAtUtc;
        return MoveRefusal.None;
    }
}
