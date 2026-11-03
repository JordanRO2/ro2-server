using RO2.DungeonFinder.Domain;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>Maps a live <see cref="PlayerState"/> to its finder <see cref="SeatKey"/> (the Domain type knows no PlayerState).</summary>
public static class SeatKeys
{
    public static SeatKey Of(PlayerState player) => new(player.CharacterId, player.SessionId, player.Incarnation);
}
