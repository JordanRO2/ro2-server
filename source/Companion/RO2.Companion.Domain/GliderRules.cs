using System.Diagnostics.CodeAnalysis;
using RO2.GameData.Tables;
using RO2.Worlds.Domain;

namespace RO2.Companions.Domain;

/// <summary>Outcome of the 1528 ReqGliderTeleport admission gate, in the order the gate tests them.</summary>
public enum GliderTeleportAdmission
{
    /// <summary>Station is not a far-glider station (unknown id or a ride row).</summary>
    UnknownStation,

    /// <summary>A second 1528 inside the de-dupe window of the last admitted one.</summary>
    Duplicate,

    /// <summary>No spline row, or the row starts on another map than the player's.</summary>
    WrongMap,

    /// <summary>A cross-map hop with no valid 1515 flight token (the token has been consumed or never existed).</summary>
    NoToken,

    /// <summary>Admitted: the host stamps the de-dupe window, then charges the fare and warps.</summary>
    Ok,
}

/// <summary>
/// Pure glider admission and fare decisions of 1313 TakeGlider, 1515 TakeFarGlider, 1528 GliderTeleport and 1314
/// GetOffGlider. The route table, the wallet, the registries and the wire stay in the host.
/// </summary>
public static class GliderRules
{
    /// <summary>1528 de-dupe window: the client sends 1528 twice per flight, ~16 ms apart.</summary>
    public const double DuplicateWindowSeconds = 3.0;

    /// <summary>
    /// 1313 gate: the kind is the ride npc_id, the spline row is the ride row of <paramref name="route"/> starting on the
    /// rider's map, and no ride is held. <paramref name="spline"/> is the host's row lookup (null when the route is not a ride).
    /// </summary>
    public static bool Admit1313(uint kind, ushort route, uint mapId, bool hasRide, [NotNullWhen(true)] Spline? spline)
        => kind == GliderRouteTable.RideNpcId && spline is not null && spline.index == route
           && spline.npc_id == GliderRouteTable.RideNpcId && spline.smap_index == mapId && !hasRide;

    /// <summary>1515 gate: a far-glider station that starts on the player's map.</summary>
    public static bool Admit1515(bool tryGet, bool startsOnMap) => tryGet && startsOnMap;

    /// <summary>
    /// 1528 gate. The checks run in order and <paramref name="consumeFlightToken"/> runs only for a cross-map hop that got that far, so a
    /// refusal earlier never burns the token. A same-map hop (smap == emap) is exempt from the token.
    /// </summary>
    public static GliderTeleportAdmission Admit1528(bool stationKnown, DateTime now, DateTime lastWarpUtc,
        Spline? spline, uint mapId, Func<bool> consumeFlightToken)
    {
        if (!stationKnown) return GliderTeleportAdmission.UnknownStation;
        if ((now - lastWarpUtc).TotalSeconds < DuplicateWindowSeconds) return GliderTeleportAdmission.Duplicate;
        if (spline is null || spline.smap_index != mapId) return GliderTeleportAdmission.WrongMap;
        if (spline.smap_index != spline.emap_index && !consumeFlightToken()) return GliderTeleportAdmission.NoToken;
        return GliderTeleportAdmission.Ok;
    }

    /// <summary>
    /// 1314 dismount fare: <c>min(cost, balance)</c>, 0 for a non-positive cost. A deliberate clamp, never a refusal: a refused
    /// dismount would leave the rider stuck mounted. The host takes it atomically with PlayerState.DebitUpTo.
    /// </summary>
    public static long RideFare(long cost, long balance) => cost <= 0 ? 0 : Math.Min(cost, balance);

    /// <summary>
    /// 1528 flight fare: the full <paramref name="cost"/> (0 when there is none), or null when the balance cannot pay it, which
    /// refuses the flight. Never clamped. The host takes it atomically with PlayerState.TryDebit.
    /// </summary>
    public static long? FarFare(long cost, long balance)
        => cost <= 0 ? 0 : balance >= cost ? cost : null;

    /// <summary>Where the rider stands after the dismount: the route end when it lands on the rider's map, else where the server has them.</summary>
    public static (float X, float Y, float Z) DismountPosition(Spline? spline,
        uint riderMapId, float requestX, float requestY, float requestZ)
        => spline != null && spline.emap_index == riderMapId
            ? (spline.end_x, spline.end_y, spline.end_z)
            : (requestX, requestY, requestZ);
}
