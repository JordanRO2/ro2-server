using RO2.GameData.Tables;
using RO2.Worlds.Domain;
using Xunit;

namespace RO2.Tests.World;

/// <summary>Characterization of <see cref="GliderRouteTable"/> over synthetic rows (the route-table rules moved out of GliderRoutes in B5 S12).</summary>
public class GliderRouteTableTests
{
    private static Spline Row(byte index, uint npcId, ushort smap, float sx, float sy, ushort emap, float ex, float ey, float ez)
        => new() { index = index, npc_id = npcId, smap_index = smap, start_x = sx, start_y = sy, emap_index = emap, end_x = ex, end_y = ey, end_z = ez };

    [Fact]
    public void FirstRowWinsOnDuplicateIndex()
    {
        var t = new GliderRouteTable([Row(5, 1, 1100, 0, 0, 1200, 1, 2, 3), Row(5, 1, 1100, 0, 0, 1300, 9, 9, 9)]);
        Assert.True(t.TryGet(5, out var dest));
        Assert.Equal(new GliderDest(1200, 1f, 2f, 3f), dest);
    }

    [Fact]
    public void RideRowsAreRidesNotStations()
    {
        var t = new GliderRouteTable([Row(1, GliderRouteTable.RideNpcId, 1101, 0, 0, 1101, 0, 0, 0), Row(2, 7, 1101, 0, 0, 1101, 0, 0, 0)]);
        Assert.False(t.TryGet(1, out _));
        Assert.True(t.IsRide(1));
        Assert.True(t.TryGet(2, out _));
        Assert.False(t.IsRide(2));
        Assert.False(t.IsRide(3));
    }

    [Fact]
    public void StartsOnMapAnswersForAnyKnownRow()
    {
        var t = new GliderRouteTable([Row(1, GliderRouteTable.RideNpcId, 1101, 0, 0, 1, 0, 0, 0)]);
        Assert.True(t.StartsOnMap(1, 1101));
        Assert.False(t.StartsOnMap(1, 1100));
        Assert.False(t.StartsOnMap(9, 1101));
    }

    [Fact]
    public void IsNearStart_UsesTheGivenRadiusInclusiveAndIsNaNSafe()
    {
        var t = new GliderRouteTable([Row(1, 7, 1101, 100, 100, 1, 0, 0, 0)]);
        Assert.True(t.IsNearStart(1, 1101, 103f, 104f, 5f));   // exactly 5 away
        Assert.False(t.IsNearStart(1, 1101, 103f, 104f, 4.9f));
        Assert.False(t.IsNearStart(1, 1100, 100f, 100f, 5f));  // other map
        Assert.False(t.IsNearStart(1, 1101, float.NaN, 100f, 5f));
        Assert.False(t.IsNearStart(1, 1101, 100f, 100f, float.NaN));
        Assert.False(t.IsNearStart(2, 1101, 100f, 100f, 5f));  // unknown row
    }
}
