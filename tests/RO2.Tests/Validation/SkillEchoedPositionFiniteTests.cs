using RO2.Server.Zone.Handlers.Rmi;
using Xunit;

namespace RO2.Tests.Validation;

/// <summary>
/// CB-H7: the 1508/1509 handlers echo the client's position (and the 1508 cast angle) map-wide in 2716/2718/2715.
/// A non-finite value is only ever a forged packet and crashes other clients, so <see cref="SkillStartHandler.EchoedPositionIsFinite"/>
/// drops it. A legitimate single-target frame sends 0,0,0, which must still pass.
/// </summary>
public class SkillEchoedPositionFiniteTests
{
    [Theory]
    [InlineData(0f, 0f, 0f, 0f, true)]              // single-target legitimate frame
    [InlineData(123.5f, -40f, 9.25f, 1.57f, true)]  // AoE legitimate frame
    [InlineData(float.NaN, 0f, 0f, 0f, false)]
    [InlineData(0f, float.PositiveInfinity, 0f, 0f, false)]
    [InlineData(0f, 0f, float.NegativeInfinity, 0f, false)]
    [InlineData(0f, 0f, 0f, float.NaN, false)]       // forged cast angle (1508)
    public void EchoedPositionIsFinite_RejectsOnlyNonFinite(float x, float y, float z, float angle, bool expected)
        => Assert.Equal(expected, SkillStartHandler.EchoedPositionIsFinite(x, y, z, angle));
}
