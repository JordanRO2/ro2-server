using RO2.GameData;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>
/// The move speed last sent to a player's own client, restated to the other players who can see that player
/// (2497 OnChangeActorMoveSpeed).
/// </summary>
/// <remarks>
/// <para>
/// A mounted player's speed is the mounted speed. Otherwise it is the standard world-entry speed. Every warp
/// dismounts first, so a newcomer's own speed is the standard one.
/// </para>
/// <para>
/// Speed changes caused by effects are not recomputed here.
/// </para>
/// </remarks>
public static class ActorMoveSpeed
{
 /// <summary>
 /// The speed <paramref name="player"/>'s own client was last given: its mounted speed when riding, otherwise the
 /// standard world-entry speed. Always greater than 0.
 /// </summary>
    public static float CurrentSpeedOf(ProudSession session, PlayerState player, GameDataStore store)
        => SpeedOn(RidingService.CurrentRide(session, player), player, store);

 /// <summary>
 /// The same speed for a caller that holds no session. Riding is read without side effects, and game data comes
 /// from <paramref name="store"/>, or from the loaded data when none is given.
 /// </summary>
 /// <remarks>
 /// The player's spawn record carries the same value, so a riding player's record and its speed agree.
 /// </remarks>
    public static float CurrentSpeedOf(PlayerState player, GameDataStore? store)
        => store == null ? TraitPackets.PcModalWorldEntrySpeed : SpeedOn(RidingService.RideOf(player), player, store);

    private static float SpeedOn(MountService.MountState? ride, PlayerState player, GameDataStore store)
    {
        if (ride is not null)
        {
            ActiveEffect[] active;
            lock (player.ActiveEffects)
                active = player.ActiveEffects.ToArray();
            
            
            if (RidingService.MountedSpeed(store, ride.PetActorHandle, active) is float mounted)
                return mounted;
        }
        return TraitPackets.PcModalWorldEntrySpeed;
    }

 /// <summary>The 2497 that restates <paramref name="player"/>'s speed to a viewer.</summary>
    public static byte[] BuildFor(ProudSession session, PlayerState player, GameDataStore store)
        => TraitPackets.BuildActorMoveSpeed2497(player.EntityId, CurrentSpeedOf(session, player, store));
}
