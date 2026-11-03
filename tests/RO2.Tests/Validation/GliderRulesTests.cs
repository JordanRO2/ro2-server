using RO2.Companions.Domain;
using RO2.GameData.Tables;
using RO2.Server.Zone.Models;
using Xunit;

namespace RO2.Tests.Validation;

/// <summary>The pure glider admission and fare decisions (1313 / 1515 / 1528 / 1314), without the host handlers.</summary>
public class GliderRulesTests
{
    private const uint Ride = 99990000;
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static Spline Row(byte index, ushort smap, ushort emap, uint npc = 1, ushort cost = 0)
        => new() { index = index, smap_index = smap, emap_index = emap, npc_id = npc, cost = cost };

    [Fact]
    public void Admit1313_NeedsRideKindRideRowStartMapAndNoHeldRide()
    {
        var row = Row(8, 1100, 1100, Ride);
        Assert.True(GliderRules.Admit1313(Ride, 8, 1100, false, row));
        Assert.False(GliderRules.Admit1313(Ride + 1, 8, 1100, false, row));        // forged kind
        Assert.False(GliderRules.Admit1313(Ride, 8, 1100, false, null));           // not a ride route
        Assert.False(GliderRules.Admit1313(Ride, 8, 1100, false, row with { npc_id = 5 }));
        Assert.False(GliderRules.Admit1313(Ride, 8, 1200, false, row));            // starts on another map
        Assert.False(GliderRules.Admit1313(Ride, 8, 1100, true, row));             // already mounted
    }

    [Fact]
    public void Admit1515_NeedsStationAndStartMap()
    {
        Assert.True(GliderRules.Admit1515(true, true));
        Assert.False(GliderRules.Admit1515(true, false));
        Assert.False(GliderRules.Admit1515(false, true));
    }

    [Fact]
    public void Admit1528_ChecksRunInOrderAndTheTokenIsOnlyConsumedWhenReached()
    {
        var cross = Row(15, 1100, 1200);
        var same = Row(16, 1100, 1100);
        int consumed = 0;
        Func<bool> burn(bool ok) => () => { consumed++; return ok; };

        Assert.Equal(GliderTeleportAdmission.UnknownStation,
            GliderRules.Admit1528(false, T0, T0.AddYears(-1), cross, 1100, burn(true)));
        Assert.Equal(GliderTeleportAdmission.Duplicate,
            GliderRules.Admit1528(true, T0, T0.AddSeconds(-2.9), cross, 1100, burn(true)));
        Assert.Equal(GliderTeleportAdmission.WrongMap,
            GliderRules.Admit1528(true, T0, T0.AddYears(-1), null, 1100, burn(true)));
        Assert.Equal(GliderTeleportAdmission.WrongMap,
            GliderRules.Admit1528(true, T0, T0.AddYears(-1), cross, 1300, burn(true)));
        Assert.Equal(0, consumed);                                                  // refused earlier: token untouched

        Assert.Equal(GliderTeleportAdmission.Ok,
            GliderRules.Admit1528(true, T0, T0.AddSeconds(-3), same, 1100, burn(false)));
        Assert.Equal(0, consumed);                                                  // same-map hop is exempt from the token

        Assert.Equal(GliderTeleportAdmission.NoToken,
            GliderRules.Admit1528(true, T0, T0.AddYears(-1), cross, 1100, burn(false)));
        Assert.Equal(GliderTeleportAdmission.Ok,
            GliderRules.Admit1528(true, T0, T0.AddYears(-1), cross, 1100, burn(true)));
        Assert.Equal(2, consumed);
    }

    [Theory]
    [InlineData(100, 500, 100)]
    [InlineData(100, 40, 40)]
    [InlineData(100, 0, 0)]
    [InlineData(0, 500, 0)]
    [InlineData(-5, 500, 0)]
    public void RideFare_IsAClampMatchingDebitUpTo(long cost, long balance, long expected)
    {
        Assert.Equal(expected, GliderRules.RideFare(cost, balance));
        var p = new PlayerState { Gold = balance };
        Assert.Equal(expected, p.DebitUpTo(cost));
    }

    [Theory]
    [InlineData(100, 500, 100L, true)]
    [InlineData(100, 100, 100L, true)]
    [InlineData(100, 99, null, false)]
    [InlineData(0, 0, 0L, true)]
    public void FarFare_IsFullCostOrRefusedMatchingTryDebit(long cost, long balance, long? expected, bool paid)
    {
        Assert.Equal(expected, GliderRules.FarFare(cost, balance));
        var p = new PlayerState { Gold = balance };
        Assert.Equal(paid, cost <= 0 || p.TryDebit(cost));
    }

    [Fact]
    public void DismountPosition_RouteEndOnTheRidersMapElseWhereTheServerHasThem()
    {
        var r = Row(8, 1100, 1100) with { end_x = 721f, end_y = 732f, end_z = 56f };
        Assert.Equal((721f, 732f, 56f), GliderRules.DismountPosition(r, 1100, 1f, 2f, 3f));
        Assert.Equal((1f, 2f, 3f), GliderRules.DismountPosition(null, 1100, 1f, 2f, 3f));
        Assert.Equal((1f, 2f, 3f), GliderRules.DismountPosition(r with { emap_index = 1200 }, 1100, 1f, 2f, 3f));
    }
}
