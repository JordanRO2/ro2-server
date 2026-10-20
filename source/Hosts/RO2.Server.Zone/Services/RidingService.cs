using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using RO2.Domain.GameData;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Riding-pet (vehicle) system: mounting and dismounting, with per-session state kept in <see
/// cref="MountService"/>.
/// </summary>
public static class RidingService
{
 /// <summary>
 /// Effect_Table 100 "탑승" (Group 100, Picking_Delete_Type 1): the effect a mounted PC carries.
 /// </summary>
    internal const uint RidingEffectId = 100;

 /// <summary>The speed a RidingInfo.Speed of 1.0 rides at.</summary>
    internal const float RiderBaseSpeed = 5.0f;

 /// <summary>Effect_Table Trait_Kind of a riding-speed row.</summary>
    internal const byte RidingSpeedTraitKind = 4;

 /// <summary>Effect_Table Task_Type of a riding-speed row: TraitBasicInfo 64 = Riding_Speed.</summary>
    internal const byte RidingSpeedTraitId = 64;

 /// <summary>Riding-speed values are per 10,000, so a value of 500 adds 5 percent.</summary>
    private const float RidingSpeedScale = 10000f;

 /// <summary>
 /// The <see cref="MountService"/> entry each PlayerState made (<see cref="MountAsync"/>), keyed
 /// weakly so it goes with the PlayerState, as MailService's claimed-this-entry table does.
 /// </summary>
 /// <remarks>
 /// Every 1417 world entry makes a new PlayerState (LoginPCHandler), so the PlayerState is what ties
 /// a ride to the world it was made in. See the class remarks, CHARACTER CHANGE.
 /// </remarks>
    private static readonly ConditionalWeakTable<PlayerState, MountService.MountState> RideOwners = new();

 /// <summary>The ride the player is on, or null.</summary>
    public static MountService.MountState? CurrentRide(ProudSession session, PlayerState player)
    {
        if (MountService.Get(session.SessionId) is not { } ride)
            return null;
        if (IsRideOf(player, ride))
            return ride;
        MountService.Dismount(session.SessionId);
        return null;
    }

    private static bool IsRideOf(PlayerState player, MountService.MountState ride)
        => RideOwners.TryGetValue(player, out var own) && ReferenceEquals(own, ride);

 /// <summary>
 /// The ride the player is on, read without side effects. Unlike <see cref="CurrentRide"/> it never
 /// removes stale entries.
 /// </summary>
    internal static MountService.MountState? RideOf(PlayerState player)
        => MountService.Get(player.SessionId) is { } ride && IsRideOf(player, ride) ? ride : null;

 /// <summary>
 /// The RidingInfo row the vehicle rides with: the LAST row of <paramref name="vehicleId"/> whose
 /// NationEnable has bit 0 set, or null when there is none.
 /// </summary>
 /// <remarks>Nine vehicles have two rows.</remarks>
    internal static RO2.Domain.GameData.Tables.RidingInfo? RideRow(GameDataStore store, uint vehicleId)
    {
        RO2.Domain.GameData.Tables.RidingInfo? row = null;
        foreach (var r in store.RidingInfo)
        {
            if (r.ID == vehicleId && (r.NationEnable & 1) != 0)
                row = r;
        }
        return row;
    }

 /// <summary>
 /// The mounted 2497 speed of <paramref name="vehicleId"/>: <c>RiderBaseSpeed × (Speed × (1 +
 /// Σ/10000))</c>, where Σ sums the riding-speed rows (Trait_Kind 4 / Task_Type 64,
 /// <c>Basic_Add_Value + Level_Multiply_Value × level</c>) of the rider's active effects. Null when
 /// the vehicle has no nation-enabled RidingInfo row.
 /// </summary>
 /// <remarks>
 /// The 1.05 is guild skill 130201 "고속 질주" (Guild_Skill_Level.Link_Skill_ID 4000001), whose effect
 /// 40000 carries a Trait_Kind 4 / Task_Type 64 row of +500. PRECISION.
 /// </remarks>
    internal static float? MountedSpeed(GameDataStore store, uint vehicleId, IEnumerable<ActiveEffect> effects)
    {
        if (RideRow(store, vehicleId) is not { } row || !(row.Speed > 0f))
            return null;

        float bonus = 0f;
        foreach (var e in effects)
        {
            foreach (var t in store.EffectTable)
            {
                
                
                if (t.ID == e.EffectId && t.Trait_Kind == RidingSpeedTraitKind && t.Task_Type == RidingSpeedTraitId
                    && t.Level_Multiply_Value != -1f)
                    bonus += t.Basic_Add_Value + t.Level_Multiply_Value * e.EffectLevel;
            }
        }

        float factor = 1f + bonus / RidingSpeedScale;
        float scaled = row.Speed * factor;
        float speed = RiderBaseSpeed * scaled;
        return float.IsFinite(speed) && speed > 0f ? speed : null;
    }

 /// <summary>
 /// True when casting <paramref name="skillId"/> leaves the rider mounted: a riding-boost skill
 /// (RidingSkill.Skill_ID) or a vehicle's own summon skill (RidingInfo.Skill_ID).
 /// </summary>
    internal static bool KeepsRide(GameDataStore store, uint skillId)
    {
        foreach (var r in store.RidingSkill)
        {
            if (r.Skill_ID == skillId) return true;
        }
        foreach (var r in store.RidingInfo)
        {
            if (r.Skill_ID == skillId) return true;
        }
        return false;
    }

 /// <summary>The caller sends the 2719 after it.</summary>
 /// <remarks>
 /// The 2497 is skipped, with nothing invented in its place, when the vehicle has no nation-enabled
 /// RidingInfo row: the client only offers vehicles from its own copy of that table, so that request
 /// names no real vehicle.
 /// </remarks>
    public static async Task MountAsync(ProudSession session, PlayerState player, uint vehicleId, uint skillId,
        IServiceProvider services)
    {
        var broadcast = services.GetRequiredService<BroadcastService>();
        var store = services.GetRequiredService<GameDataStore>();
        var effects = services.GetRequiredService<EffectService>();

        var ride = MountService.Mount(session.SessionId, player.EntityId, petId: skillId, petActorHandle: vehicleId);
        RideOwners.AddOrUpdate(player, ride);

        
        await SendToSelfAndNearbyAsync(session, player, broadcast, ZoneS2COpcodes.NfyRideRidingPet,
            MountPackets.BuildNfyRideRidingPet(skillId, player.EntityId, vehicleId));

        
        
        ActiveEffect[] active;
        lock (player.ActiveEffects)
            active = player.ActiveEffects.ToArray();
        if (MountedSpeed(store, vehicleId, active) is float speed)
        {
            var body = TraitPackets.BuildActorMoveSpeed2497(player.EntityId, speed);
            await broadcast.BroadcastToMapAsync(player.MapId, ZoneS2COpcodes.OnChangeActorMoveSpeed, w => w.WriteBytes(body));
        }

        
        
        await effects.ApplyEffectToPlayerAsync(player, RidingEffectId, skillId,
            casterHandle: player.EntityId, casterName: player.Name, durationMs: 0);
    }

 /// <summary>
 /// Dismounts the player if it is riding, notifying the map. Returns false and sends nothing when
 /// the player is not riding.
 /// </summary>
 /// <param name="effectAlreadyRemoved">
 /// True when the caller has already removed effect 100 itself, such as on a cancel request.
 /// </param>
    public static async Task<bool> DropAsync(ProudSession session, PlayerState player, IServiceProvider services,
        bool effectAlreadyRemoved = false)
    {
        if (MountService.Dismount(session.SessionId) is not { } ridden)
            return false;
        
        if (!IsRideOf(player, ridden))
            return false;
        RideOwners.Remove(player);

        var broadcast = services.GetRequiredService<BroadcastService>();

        var speed = TraitPackets.BuildActorMoveSpeed2497(player.EntityId, TraitPackets.PcModalWorldEntrySpeed);
        await broadcast.BroadcastToMapAsync(player.MapId, ZoneS2COpcodes.OnChangeActorMoveSpeed, w => w.WriteBytes(speed));

        if (!effectAlreadyRemoved)
            await services.GetRequiredService<EffectService>().RemovePlayerEffectAsync(player, RidingEffectId);

        
        
        await SendToSelfAndNearbyAsync(session, player, broadcast, ZoneS2COpcodes.NfyDropRidingPet,
            MountPackets.BuildNfyDropRidingPet(player.EntityId, ridden.PetId, ridden.PetActorHandle));
        return true;
    }

 /// <summary>
 /// The rider is also sent its own RMI 2720 and 2712 frames, since the nearby broadcast skips the
 /// sender.
 /// </summary>
    private static async Task SendToSelfAndNearbyAsync(ProudSession session, PlayerState player, BroadcastService broadcast,
        ushort opcode, byte[] body)
    {
        await session.SendInternalRmiAsync(opcode, body);
        await broadcast.BroadcastToNearbyAsync(player, opcode, w => w.WriteBytes(body));
    }
}
