using RO2.GameData.Tables;
using RO2.Worlds.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Glider (Kafra "flight") station -> destination map + spawn coordinates, derived from data/spline.json.
///
/// The station id the client sends (C2S 1528 ReqGliderTeleport / 1515 ReqTakeFarGlider, u16 at body+10) is the
/// spline.json <c>index</c>; the row's <c>emap_index</c> / <c>end_x</c> / <c>end_y</c> / <c>end_z</c> are where the
/// player arrives. Deriving the table from the data file covers every station, including the map-3101 same-map
/// hops (stations 87-102).
///
/// Only rows whose <c>npc_id</c> is not <see cref="RideNpcId"/> are stations: the ride rows (npc_id 99990000, the
/// 1313 TakeGlider routes) are not far-glider destinations. <see cref="IsRide"/> and <see cref="IsNearStart"/>
/// answer for any row, ride or station.
///
/// The route-table rules live in the World Domain (<see cref="GliderRouteTable"/>); this host class owns the
/// spline.json load, the process-wide table, and the unverified interaction radius.
/// </summary>
public static class GliderRoutes
{
    /// <summary>The <c>npc_id</c> of a 1313 TakeGlider ride row (also the 1313 <c>gliderKind</c> the client sends).</summary>
    public const uint RideNpcId = GliderRouteTable.RideNpcId;

    /// <summary>
    /// How far from a row's <c>start_x</c>/<c>start_y</c> a player may be to use it. UNVERIFIED: the figure is in
    /// no data table; it is the single NPC-interaction constant
    /// (<see cref="NpcInteraction.DefaultInteractRadius"/>).
    /// </summary>
    public const float NearStartRadius = NpcInteraction.DefaultInteractRadius;

    private static readonly Lazy<GliderRouteTable> Default = new(() => Load(Path.Combine("data", "spline.json")));

    /// <summary>Resolve a glider station id to its destination, or false if it is unknown or is a ride row.</summary>
    public static bool TryGet(ushort station, out GliderDest dest) => Default.Value.TryGet(station, out dest);

    /// <summary>Whether <paramref name="index"/> is a spline.json ride row (npc_id == <see cref="RideNpcId"/>).</summary>
    public static bool IsRide(ushort index) => Default.Value.IsRide(index);

    /// <summary>Whether row <paramref name="index"/> starts on <paramref name="mapId"/> (<c>smap_index</c>): the hard
    /// same-map part of <see cref="IsNearStart"/>, without its unverified radius. False for an unknown row.</summary>
    public static bool StartsOnMap(ushort index, uint mapId) => Default.Value.StartsOnMap(index, mapId);

    /// <summary>Whether a player at (<paramref name="x"/>, <paramref name="y"/>) on <paramref name="mapId"/> is within
    /// <see cref="NearStartRadius"/> of the start of row <paramref name="index"/> on that row's start map
    /// (<c>smap_index</c>). False for an unknown row. NaN-safe.</summary>
    public static bool IsNearStart(ushort index, uint mapId, float x, float y) => Default.Value.IsNearStart(index, mapId, x, y, NearStartRadius);

    /// <summary>The route table of the spline.json at <paramref name="path"/>; empty (every lookup false) when the
    /// file is missing or unreadable.</summary>
    internal static GliderRouteTable Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new GliderRouteTable([]);
            return new GliderRouteTable(System.Text.Json.JsonSerializer.Deserialize<List<Spline>>(File.ReadAllBytes(path)) ?? []);
        }
        catch
        {
            return new GliderRouteTable([]);
        }
    }
}
