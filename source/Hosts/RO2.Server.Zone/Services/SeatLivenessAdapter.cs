using RO2.DungeonFinder.Application;
using RO2.DungeonFinder.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// The finder's liveness port over <see cref="PlayerStateManager"/>. A session hosts one character lifetime and a relog is a
/// new session id, so "this session still carries this character" is the same test as the old PlayerState reference check.
/// </summary>
public sealed class SeatLivenessAdapter(PlayerStateManager players) : ISeatLiveness
{
    public bool IsLive(SeatKey seat) => players.GetPlayer(seat.SessionId)?.CharacterId == seat.CharacterId;
}
