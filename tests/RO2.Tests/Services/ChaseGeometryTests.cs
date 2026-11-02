using RO2.Monster.Domain;
using Xunit;

namespace RO2.Tests.Services;

/// <summary>The pure chase geometry: step toward a target, the reach/gap/step decision, and the leash-distance predicate.</summary>
public class ChaseGeometryTests
{
    [Fact]
    public void StepToward_PartialStep_MovesAlongTheLine()
    {
        var (x, y) = ChaseGeometry.StepToward(0f, 0f, 3f, 4f, 2.5f);   // dist 5, half way
        Assert.Equal(1.5f, x, 3);
        Assert.Equal(2.0f, y, 3);
    }

    [Fact]
    public void StepToward_StepPastTarget_StopsAtTarget()
    {
        var (x, y) = ChaseGeometry.StepToward(0f, 0f, 3f, 4f, 100f);   // step >> dist: clamp to the target
        Assert.Equal(3f, x, 3);
        Assert.Equal(4f, y, 3);
    }

    [Fact]
    public void StepToward_Coincident_ReturnsOrigin()
    {
        var (x, y) = ChaseGeometry.StepToward(7f, 9f, 7f, 9f, 3f);
        Assert.Equal(7f, x, 3);
        Assert.Equal(9f, y, 3);
    }

    [Fact]
    public void StepToward_NonFiniteTarget_ReturnsOrigin()
    {
        var (x, y) = ChaseGeometry.StepToward(1f, 2f, float.NaN, 5f, 3f);
        Assert.Equal(1f, x);
        Assert.Equal(2f, y);
    }

    [Fact]
    public void DecideStep_InsideReach_Stays()
    {
        Assert.Null(ChaseGeometry.DecideStep(0f, 0f, 3f, 4f, 5f, 4f, 5f, 0.45));      // gap 5 == reach 5
        Assert.Null(ChaseGeometry.DecideStep(0f, 0f, 3f, 4f, 6f, 4f, 5f, 0.45));      // gap 5 < reach 6
    }

    [Fact]
    public void DecideStep_NonFiniteTarget_Stays()
    {
        Assert.Null(ChaseGeometry.DecideStep(0f, 0f, float.NaN, 4f, 0f, 4f, 5f, 0.45));
        Assert.Null(ChaseGeometry.DecideStep(0f, 0f, float.PositiveInfinity, 4f, 0f, 4f, 5f, 0.45));
    }

    [Fact]
    public void DecideStep_SpeedBoundStep_MovesSpeedTimesDt()
    {
        // gap 10, reach 4 (fallback, AttackReach 0): remaining 6; speed*dt = 5*0.45 = 2.25 < 6, so the step is 2.25.
        var p = ChaseGeometry.DecideStep(0f, 0f, 10f, 0f, 0f, 4f, 5f, 0.45);
        Assert.NotNull(p);
        Assert.Equal(2.25f, p!.Value.X, 3);
        Assert.Equal(0f, p.Value.Y, 3);
    }

    [Fact]
    public void DecideStep_GapBoundStep_StopsAtReach()
    {
        // gap 10, own reach 9: remaining 1 < speed*dt 2.25, so it lands exactly at reach distance from the target.
        var p = ChaseGeometry.DecideStep(0f, 0f, 10f, 0f, 9f, 4f, 5f, 0.45);
        Assert.NotNull(p);
        Assert.Equal(1f, p!.Value.X, 3);
    }

    [Fact]
    public void DecideStep_NonPositiveSpeed_Stays()
    {
        Assert.Null(ChaseGeometry.DecideStep(0f, 0f, 10f, 0f, 0f, 4f, 0f, 0.45));
        Assert.Null(ChaseGeometry.DecideStep(0f, 0f, 10f, 0f, 0f, 4f, 5f, 0.0));
    }

    [Fact]
    public void IsPastLeash_StrictlyGreaterThanBound()
    {
        Assert.False(ChaseGeometry.IsPastLeash(3f, 4f, 0f, 0f, 5f));   // distance 5 == bound: not past
        Assert.True(ChaseGeometry.IsPastLeash(3f, 4f, 0f, 0f, 4.9f));
    }

    [Fact]
    public void IsPastLeash_NoBound_NeverLeashes()
    {
        Assert.False(ChaseGeometry.IsPastLeash(1000f, 1000f, 0f, 0f, 0f));
        Assert.False(ChaseGeometry.IsPastLeash(1000f, 1000f, 0f, 0f, -1f));
    }
}
