using RO2.GameData;
using RO2.Worlds.Domain;

namespace RO2.Worlds.Application;

/// <summary>
/// The decision of a 1615 ReqMultiTeleportNpc: accept (2905 result 0 and the warp) or refuse (2905 result 24323), over the
/// pure <see cref="WarpDestinationRules"/>. Pure: the host reads the live player and NPC, sends the answer and runs the
/// departure and the warp in its own original order.
/// </summary>
public static class MultiTeleportUseCase
{
    /// <summary>Length of a 1615 request: the VarBlob32 length prefix (4), the NPC handle (4) and the slot (1). A shorter body is refused.</summary>
    public const int RequestLength = 4 + 4 + 1;

    /// <summary>2905 result: accepted.</summary>
    public const ushort ResultAccepted = 0;

    /// <summary>2905 result 24323 (0x5F03): string_ui 11109 "Only usable by the members of the victorious guild.".</summary>
    public const ushort ResultRefused = 24323;

    /// <summary>Why a request is refused. The refusals are checked in this order.</summary>
    public enum Refusal
    {
        /// <summary>Accepted.</summary>
        None,

        /// <summary>The body is shorter than <see cref="RequestLength"/>.</summary>
        ShortRequest,

        /// <summary>The player is dead.</summary>
        PlayerDead,

        /// <summary>The handle is not an NPC this server tracks on the player's map.</summary>
        NotNpcOnMap,

        /// <summary>The NPC has no resolvable (group, slot) destination.</summary>
        NoDestination,
    }

    /// <summary>A tracked NPC as the decision sees it.</summary>
    /// <param name="NpcId">The NPCInfo id.</param>
    /// <param name="MapId">The map the NPC stands on.</param>
    public readonly record struct NpcRef(uint NpcId, uint MapId);

    /// <summary>The decision.</summary>
    /// <param name="Why">The refusal, or <see cref="Refusal.None"/> when accepted.</param>
    /// <param name="Destination">The Position_Info row to warp to; set only when accepted.</param>
    public readonly record struct Decision(Refusal Why, PositionData? Destination)
    {
        /// <summary>True when the request is accepted.</summary>
        public bool Accepted => Why == Refusal.None;

        /// <summary>The 2905 result code.</summary>
        public ushort Result => Accepted ? ResultAccepted : ResultRefused;
    }

    /// <summary>
    /// Decides a request: too short, then a dead player, then an NPC that is not on the player's map, then a slot with no
    /// destination, are refused; anything else is accepted.
    /// </summary>
    public static Decision Decide(IWorldCatalog catalog, int payloadLength, bool playerDead, uint playerMapId, NpcRef? npc, byte slot)
    {
        if (payloadLength < RequestLength) return new Decision(Refusal.ShortRequest, null);
        if (playerDead) return new Decision(Refusal.PlayerDead, null);
        if (npc is not { } n || n.MapId != playerMapId) return new Decision(Refusal.NotNpcOnMap, null);
        return WarpDestinationRules.TryResolveMultiTeleportDestination(catalog, n.NpcId, slot, out var dest)
            ? new Decision(Refusal.None, dest)
            : new Decision(Refusal.NoDestination, null);
    }
}
