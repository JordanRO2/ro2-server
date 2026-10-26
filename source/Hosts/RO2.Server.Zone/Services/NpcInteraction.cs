using RO2.Server.Zone.Models;
using RO2.Worlds.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// The "player is standing at the NPC" precondition of the NPC-anchored movement requests (1371 Warp, 1615
/// MultiTeleportNpc). Pure: positions in, bool out, no state, no clock.
/// </summary>
public static class NpcInteraction
{
    /// <summary>
    /// How far from an NPC a player may stand and still use it. A server-side defensive figure: the radius is in
    /// no data table. It is the same loose figure <see cref="ActorGate.CanTransact"/> callers use, so there is ONE
    /// interaction constant; same map stays the hard rule, this only keeps a request from a far-away player out.
    /// </summary>
    public const float DefaultInteractRadius = ActorGate.NpcInteractReach;

    /// <summary>
    /// Whether <paramref name="player"/> is on <paramref name="npc"/>'s map and within <paramref name="radius"/>
    /// of it, measured horizontally (X, Y) as <see cref="ActorGate"/> does. NaN-safe: a non-finite position or a
    /// NaN radius fails the comparison and answers false. Liveness is not asked (callers gate dead players).
    /// </summary>
    public static bool GateInReach(PlayerState player, NpcRuntimeState npc, float radius)
        => NpcInteractionRules.InReach(player.MapId, player.X, player.Y, npc.MapId, npc.PosX, npc.PosY, radius);
}
