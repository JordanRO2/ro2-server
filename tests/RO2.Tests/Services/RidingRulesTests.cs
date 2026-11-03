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
}
