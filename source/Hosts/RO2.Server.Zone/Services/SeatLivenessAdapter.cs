using RO2.DungeonFinder.Application;
using RO2.DungeonFinder.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// The finder's liveness port over <see cref="PlayerStateManager"/>. The incarnation is unique per PlayerState, so
/// "the session's player has the seat's incarnation" is exactly the old PlayerState reference check (a 1417 re-login on the
/// same session builds a new PlayerState and so goes stale).
/// </summary>
public sealed class SeatLivenessAdapter(PlayerStateManager players) : ISeatLiveness
{
    public bool IsLive(SeatKey seat) => players.GetPlayer(seat.SessionId)?.Incarnation == seat.Incarnation;
}
