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

    public MovementGuard(IClock clock) => _clock = clock;

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

    private MoveRefusal Apply(PlayerState player, MovePos dest, float speed, bool serverGrantedExemption,
        MoveBounds? bounds, IWalkableSurface? surface)
    {
        var now = _clock.UtcNow;
        var since = player.LastValidatedMoveAtUtc ?? now;
        var dt = (float)(now - since).TotalSeconds;
        if (dt < 0f) dt = 0f; 

        var current = new MovePos(player.X, player.Y, player.Z);
        var refusal = MovementValidator.Validate(player.MapId, current, dest, dt, speed, bounds, surface, serverGrantedExemption);
        if (refusal != MoveRefusal.None)
            return refusal;

        player.X = dest.X;
        player.Y = dest.Y;
        player.Z = dest.Z;
        player.LastValidatedMoveAtUtc = now;
        return MoveRefusal.None;
    }
}
