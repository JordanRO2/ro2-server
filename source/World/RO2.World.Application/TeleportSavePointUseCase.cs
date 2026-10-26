using RO2.Worlds.Domain;

namespace RO2.Worlds.Application;

/// <summary>
/// The decisions of a 1677 ReqTeleportSavePoint (/escape): whether the request is ignored, and where and in which form the
/// player goes. Pure: the host claims and releases the cooldown, loads the data and sends the frames in its own original order.
/// </summary>
public static class TeleportSavePointUseCase
{
    /// <summary>The destination of an escape.</summary>
    /// <param name="MapId">The destination map.</param>
    /// <param name="X">The landing x.</param>
    /// <param name="Y">The landing y.</param>
    /// <param name="Z">The landing z.</param>
    /// <param name="Source">Which save-point rule chose it, for the log.</param>
    /// <param name="MapKnown">False when the destination map is not in Map_List: nothing is sent and the cooldown is given back.</param>
    public readonly record struct Decision(uint MapId, float X, float Y, float Z, string Source, bool MapKnown);

    /// <summary>
    /// A dead, fighting or trading player's /escape is ignored: no refusal is sent and the cooldown is not consumed.
    /// </summary>
    public static bool IsIgnored(bool playerDead, bool inCombat, bool trading) => playerDead || inCombat || trading;

    /// <summary>Where the player goes (<see cref="WarpDestinationRules.ResolveSavePoint"/>) and whether that map exists.</summary>
    public static Decision Decide(IWorldCatalog catalog, uint playerMapId, ushort bindIndex)
    {
        var (mapId, x, y, z, source) = WarpDestinationRules.ResolveSavePoint(playerMapId, bindIndex, catalog);
        return new Decision(mapId, x, y, z, source, catalog.GetMap(mapId) is not null);
    }

    /// <summary>
    /// Whether the escape stays on the player's own map, which takes the in-place form instead of the map-change form. The
    /// host asks it at the point it sends, with the player's map as it is then.
    /// </summary>
    public static bool IsInPlace(uint destinationMapId, uint playerMapId) => destinationMapId == playerMapId;
}
