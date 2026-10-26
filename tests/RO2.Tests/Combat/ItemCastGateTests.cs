using RO2.Combats.Application;
using Xunit;
using D = RO2.Combats.Application.ItemCastGate.Decision;

namespace RO2.Tests.Combat;

/// <summary>Characterization of <see cref="ItemCastGate.Check"/> (the 1561 item-cast authorization moved into Combat.Application in B4 S23).</summary>
public class ItemCastGateTests
{
    [Fact]
    public void NotOwned_RefusedFirst()
        => Assert.Equal(D.NotOwned, ItemCastGate.Check(ownedByCaster: false, inBag: false, skillMatches: false,
            warpDestinationRequired: true, warpDestinationResolved: false));

    [Fact]
    public void NotInBag_AfterOwned()
        => Assert.Equal(D.NotInBag, ItemCastGate.Check(true, inBag: false, skillMatches: false, true, false));

    [Fact]
    public void SkillMismatch_AfterBag()
        => Assert.Equal(D.SkillMismatch, ItemCastGate.Check(true, true, skillMatches: false, true, false));

    [Fact]
    public void WarpRequiredButUnresolved_Refused()
        => Assert.Equal(D.NoWarpDestination, ItemCastGate.Check(true, true, true,
            warpDestinationRequired: true, warpDestinationResolved: false));

    [Fact]
    public void WarpNotRequired_Allowed()
        => Assert.Equal(D.Allowed, ItemCastGate.Check(true, true, true,
            warpDestinationRequired: false, warpDestinationResolved: false));

    [Fact]
    public void WarpRequiredAndResolved_Allowed()
        => Assert.Equal(D.Allowed, ItemCastGate.Check(true, true, true,
            warpDestinationRequired: true, warpDestinationResolved: true));

    [Fact]
    public void NonWarpItem_Allowed()
        => Assert.Equal(D.Allowed, ItemCastGate.Check(true, true, true, false, false));
}
