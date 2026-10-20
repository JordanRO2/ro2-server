using RO2.Network.Protocol;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Sends a PC's bars as 2401 OnActorAutoHeal to everyone who can see the PC, the PC included.
/// </summary>
/// <remarks>
/// <para>
/// Every regeneration, heal and revival 2401 goes to the owner and to every PC that has the owner in view, so
/// other players see a PC's bars move.
/// </para>
/// <para>
/// A map broadcast with no exclusion covers that audience here: this server has no interest management and every
/// PC on a map is shown every other one (PlayerSpawnService.NotifyPlayerSpawnAsync). A copy that reaches a viewer
/// which does not hold the actor yet is harmless: the client looks the actor up by the id at blob+8 and updates
/// that actor only. The owner's own copy is unchanged on the wire: BroadcastService uses
/// <c>SendInternalRmiAsync</c>, the same unencrypted path <c>SendRmiAsync</c> delegates to.
/// </para>
/// <para>
/// The body is <see cref="ActorVitalsPackets.BuildActorAutoHeal2401"/>. NPC-form 2401s (blob+4 = 0, blob+8 the
/// NPC handle) are only produced for monster resets: this server has no NPC regeneration.
/// </para>
/// </remarks>
public static class ActorVitalsBroadcast
{
    /// <summary>
    /// Broadcasts <paramref name="player"/>'s current HP/SP and maxima as 2401 to every session on its map,
    /// <paramref name="player"/>'s own included.
    /// </summary>
    public static Task SendAutoHealAsync(BroadcastService broadcast, PlayerState player)
    {
        var body = ActorVitalsPackets.BuildActorAutoHeal2401(
            player.EntityId, player.Hp, player.Mp, player.MaxHp, player.MaxMp);
        return broadcast.BroadcastToMapAsync(player.MapId, ZoneS2COpcodes.OnActorAutoHeal,
            w => w.WriteBytes(body));
    }

    /// <summary>
    /// Broadcasts a monster's current HP/SP as the NPC-form 2401 (blob+4 = 0, blob+8 = the NPC handle) to every
    /// session on its map. Used on a leash/attacker-loss reset so the floating HP bar shows the restored HP without
    /// the player re-attacking: the client writes the bars straight into the actor it finds by blob+8, with no
    /// need for the actor to be re-targeted first.
    /// </summary>
    public static Task SendNpcAutoHealAsync(BroadcastService broadcast, NpcRuntimeState npc)
    {
        var body = ActorVitalsPackets.BuildActorAutoHeal2401(
            npc.EntityOid, npc.Hp, npc.WireCurSp, npc.MaxHp, npc.WireMaxSp, isPc: false);
        return broadcast.BroadcastToMapAsync(npc.MapId, ZoneS2COpcodes.OnActorAutoHeal,
            w => w.WriteBytes(body));
    }
}
