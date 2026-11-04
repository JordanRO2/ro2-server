using RO2.Companions.Domain;
using Xunit;

namespace RO2.Tests.Services;

/// <summary>The pure 1510 mount-toggle decision and its 2719 result codes.</summary>
public class RidingRulesTests
{
    [Theory]
    [InlineData(null, 5u, RideAction.Mount, (ushort)0)]
    [InlineData(5u, 5u, RideAction.DropSame, (ushort)21801)]
    [InlineData(5u, 6u, RideAction.DropSwitch, (ushort)17)]
    public void DecideMapsTheRideToAnActionAndResult(uint? current, uint requested, RideAction action, ushort result)
    {
        var decided = RidingRules.Decide(current, requested);
        Assert.Equal(action, decided);
        Assert.Equal(result, RidingRules.ResultCode(decided));
    }

    [Theory]
    [InlineData(5u, 0.0, true)]      // permanent
    [InlineData(5u, 200.0, true)]    // timed, still valid
    [InlineData(5u, 50.0, false)]    // expired
    [InlineData(9u, 0.0, false)]     // not owned
    public void CanRideNeedsAnOwnedUnexpiredVehicle(uint id, double expiry, bool expected)
    {
        var owned = new[] { new OwnedVehicle { RidingId = 5, ExpiryOle = expiry } };
        Assert.Equal(expected, VehicleRules.CanRide(owned, id, 100.0));
    }
}
