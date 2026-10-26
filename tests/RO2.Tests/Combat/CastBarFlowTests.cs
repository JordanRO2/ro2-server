using RO2.Combats.Application;
using Xunit;
using O = RO2.Combats.Application.CastBarFlow.StartOutcome;

namespace RO2.Tests.Combat;

/// <summary>Characterization of <see cref="CastBarFlow.CheckStart"/> (the 1506 cast-bar precondition moved into Combat.Application in B4 S20).</summary>
public class CastBarFlowTests
{
    [Fact]
    public void ReadyCasterFinitePosition_Allowed()
        => Assert.Equal(O.Allowed, CastBarFlow.CheckStart(casterMapLoaded: true, casterIsDead: false, 10f, 20f, 30f));

    [Theory]
    [InlineData(false, false)] // not map-loaded
    [InlineData(true, true)]   // dead
    [InlineData(false, true)]  // both
    public void NotLoadedOrDead_CasterNotReady(bool mapLoaded, bool dead)
        => Assert.Equal(O.CasterNotReady, CastBarFlow.CheckStart(mapLoaded, dead, 1f, 2f, 3f));

    [Fact]
    public void CasterCheckPrecedesPositionCheck()
        // A dead caster with a NaN position is CasterNotReady, not NonFinitePosition (caster is vetted first).
        => Assert.Equal(O.CasterNotReady, CastBarFlow.CheckStart(true, casterIsDead: true, float.NaN, 0f, 0f));

    [Theory]
    [InlineData(float.NaN, 0f, 0f)]
    [InlineData(0f, float.PositiveInfinity, 0f)]
    [InlineData(0f, 0f, float.NegativeInfinity)]
    public void NonFiniteCoordinate_Refused(float x, float y, float z)
        => Assert.Equal(O.NonFinitePosition, CastBarFlow.CheckStart(true, false, x, y, z));
}
