using RO2.Worlds.Domain;
using Xunit;

namespace RO2.Tests.World;

/// <summary>
/// The pure area-of-interest rule: the half-width per map type and the square in-view test, as measured from the retail
/// corpus (Chebyshev on X/Y, half-width 60 standard / 200 wide, strict edge, Z ignored).
/// </summary>
public class AoiRulesTests
{
    [Theory]
    [InlineData(0, 60f)]
    [InlineData(1, 60f)]
    [InlineData(3, 60f)]
    [InlineData(4, 200f)]   // WoE
    [InlineData(5, 200f)]   // open field
    [InlineData(6, 60f)]
    public void HalfWidth_IsWideOnlyForTypes4And5(byte mapType, float expected)
        => Assert.Equal(expected, AoiRules.HalfWidth(mapType));

    [Fact]
    public void InView_IncludesTheCornerJustInsideAndExcludesTheBound()
    {
        // A corner at |dx| = |dy| = 59 is in view; 60 is out (strict less-than).
        Assert.True(AoiRules.InView(1000f, 1000f, 1059f, 1059f, 60f));
        Assert.False(AoiRules.InView(1000f, 1000f, 1060f, 1000f, 60f));
        Assert.False(AoiRules.InView(1000f, 1000f, 1000f, 1060f, 60f));
    }

    [Fact]
    public void InView_IgnoresZ()
    {
        // The rule takes only X/Y; the same X/Y is in view whatever the height difference (there is no Z parameter).
        Assert.True(AoiRules.InView(0f, 0f, 10f, 10f, 60f));
        Assert.False(AoiRules.InView(0f, 0f, 100f, 0f, 60f));
    }

    [Fact]
    public void InView_IsSymmetric_EnterEqualsLeave()
    {
        // No hysteresis: a point in view from the viewer is in view from the actor with the same half-width.
        Assert.Equal(
            AoiRules.InView(0f, 0f, 55f, 20f, 60f),
            AoiRules.InView(55f, 20f, 0f, 0f, 60f));
    }
}
