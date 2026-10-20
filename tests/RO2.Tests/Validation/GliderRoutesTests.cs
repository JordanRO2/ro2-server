using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Validation;

/// <summary>
/// GliderRoutes is derived from data/spline.json. The golden set is the OLD hand-copied
/// 39-entry table: every one must resolve identically from the data.
/// </summary>
public class GliderRoutesTests
{
    // The retired GliderRoutes._routes table, verbatim: station -> (map, x, y, z).
    private static readonly (ushort Station, uint Map, float X, float Y, float Z)[] OldTable =
    [
        (15, 1200, 728f, 546f, 56f), (16, 1101, 1314f, 680f, 54f), (17, 1111, 473f, 1572f, 39f),
        (18, 1111, 1527f, 1257f, 114f), (19, 1111, 1576f, 701f, 64f), (20, 1201, 448f, 1602f, 17f),
        (21, 1201, 551f, 503f, 70f), (22, 1211, 1657f, 1021f, 56f), (23, 1211, 419f, 194f, 21f),
        (24, 1211, 598f, 1552f, 31f), (65, 1100, 801f, 767f, 58f), (66, 1101, 1314f, 680f, 54f),
        (67, 1111, 473f, 1572f, 39f), (68, 1111, 1527f, 1257f, 114f), (69, 1111, 1576f, 701f, 64f),
        (70, 1201, 448f, 1602f, 17f), (71, 1201, 551f, 503f, 70f), (72, 1211, 1657f, 1021f, 56f),
        (73, 1211, 419f, 194f, 21f), (74, 1211, 598f, 1552f, 32f), (103, 1300, 446f, 210f, 12f),
        (104, 1301, 1003f, 248f, 15f), (105, 1300, 446f, 210f, 12f), (106, 1301, 1003f, 248f, 15f),
        (127, 1211, 598f, 1552f, 31f), (128, 1301, 1003f, 248f, 15f), (129, 1100, 801f, 767f, 58f),
        (130, 1101, 502f, 248f, 37f), (131, 1111, 473f, 1572f, 39f), (132, 1111, 1527f, 1257f, 114f),
        (133, 1111, 1576f, 701f, 64f), (134, 1201, 448f, 1602f, 17f), (135, 1201, 551f, 503f, 70f),
        (136, 1211, 1657f, 1021f, 56f), (137, 1211, 419f, 194f, 21f), (138, 1200, 728f, 546f, 56f),
        (139, 1101, 1314f, 680f, 54f), (140, 1101, 502f, 248f, 37f), (141, 1101, 502f, 248f, 37f),
    ];

    private static GliderRoutes.RouteTable Table()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "data", "spline.json")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return GliderRoutes.Load(Path.Combine(dir!.FullName, "data", "spline.json"));
    }

    [Fact]
    public void OldTable_Has39Entries()
        => Assert.Equal(39, OldTable.Length);

    [Fact]
    public void Derived_ResolvesEveryOldEntryIdentically()
    {
        var t = Table();
        foreach (var (station, map, x, y, z) in OldTable)
        {
            Assert.True(t.TryGet(station, out var dest), $"station {station}");
            Assert.Equal(new GliderRoutes.Dest(map, x, y, z), dest);
        }
    }

    [Fact]
    public void Rides_AreNotStationsButAreRides()
    {
        var t = Table();
        Assert.False(t.TryGet(1, out _)); // row 1 is a 99990000 ride row
        Assert.True(t.IsRide(1));
        Assert.False(t.IsRide(15));
        Assert.False(t.TryGet(0, out _));
        Assert.False(t.TryGet(60000, out _)); // beyond the byte index
        Assert.False(t.IsRide(60000));
    }

    [Fact]
    public void IsNearStart_RequiresTheStartMapAndTheRadius()
    {
        var t = Table();
        // spline row 1: smap 1101, start (460, 292)
        Assert.True(t.IsNearStart(1, 1101, 460f, 292f));
        Assert.True(t.IsNearStart(1, 1101, 460f + GliderRoutes.NearStartRadius - 1f, 292f));
        Assert.False(t.IsNearStart(1, 1101, 460f + GliderRoutes.NearStartRadius + 1f, 292f));
        Assert.False(t.IsNearStart(1, 1100, 460f, 292f));       // other map
        Assert.False(t.IsNearStart(1, 1101, float.NaN, 292f));  // NaN-safe
        Assert.False(t.IsNearStart(60000, 1101, 460f, 292f));   // unknown row
    }
}
