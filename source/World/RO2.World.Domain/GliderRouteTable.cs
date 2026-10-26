using RO2.GameData.Tables;

namespace RO2.Worlds.Domain;

/// <summary>Where a glider station lands the player: destination map and spawn coordinates.</summary>
public readonly record struct GliderDest(uint MapId, float X, float Y, float Z);

/// <summary>
/// The glider (Kafra "flight") route rows of spline.json indexed by <c>index</c> (first row wins on a duplicate), and
/// the pure lookups over them. The rows are handed in by the host, which owns reading the data file; the interaction
/// radius of <see cref="IsNearStart"/> is passed in per call, as the figure is in no data table.
///
/// Only rows whose <c>npc_id</c> is not <see cref="RideNpcId"/> are stations: the ride rows (the 1313 TakeGlider
/// routes) are not far-glider destinations. <see cref="IsRide"/>, <see cref="StartsOnMap"/> and
/// <see cref="IsNearStart"/> answer for any row, ride or station.
/// </summary>
public sealed class GliderRouteTable
{
    /// <summary>The <c>npc_id</c> of a 1313 TakeGlider ride row (also the 1313 <c>gliderKind</c> the client sends).</summary>
    public const uint RideNpcId = 99990000;

    private readonly Dictionary<ushort, Spline> _rows = new();

    /// <summary>Index <paramref name="rows"/> by <c>index</c>; the first row wins on a duplicate.</summary>
    public GliderRouteTable(IEnumerable<Spline> rows)
    {
        foreach (var r in rows)
            _rows.TryAdd(r.index, r);
    }

    /// <summary>Resolve a glider station id to its destination, or false if it is unknown or is a ride row.</summary>
    public bool TryGet(ushort station, out GliderDest dest)
    {
        if (_rows.TryGetValue(station, out var r) && r.npc_id != RideNpcId)
        {
            dest = new GliderDest(r.emap_index, r.end_x, r.end_y, r.end_z);
            return true;
        }
        dest = default;
        return false;
    }

    /// <summary>Whether <paramref name="index"/> is a ride row (npc_id == <see cref="RideNpcId"/>).</summary>
    public bool IsRide(ushort index) => _rows.TryGetValue(index, out var r) && r.npc_id == RideNpcId;

    /// <summary>Whether row <paramref name="index"/> starts on <paramref name="mapId"/> (<c>smap_index</c>). False for
    /// an unknown row.</summary>
    public bool StartsOnMap(ushort index, uint mapId) => _rows.TryGetValue(index, out var r) && r.smap_index == mapId;

    /// <summary>Whether a player at (<paramref name="x"/>, <paramref name="y"/>) on <paramref name="mapId"/> is within
    /// <paramref name="radius"/> of the start of row <paramref name="index"/> on that row's start map
    /// (<c>smap_index</c>). False for an unknown row. NaN-safe.</summary>
    public bool IsNearStart(ushort index, uint mapId, float x, float y, float radius)
    {
        if (!_rows.TryGetValue(index, out var r) || r.smap_index != mapId) return false;
        var dx = r.start_x - x;
        var dy = r.start_y - y;
        // Written so that NaN positions fail the comparison and are refused.
        return MathF.Sqrt(dx * dx + dy * dy) <= radius;
    }
}
