using RO2.Monster.Application;
using Xunit;

namespace RO2.Tests.Monster;

/// <summary>The per-tick chase decision: leash before chase, lazy target lookup, and the gap-closing step.</summary>
public class ChasePassTests
{
    private const uint Map = 1100;

    private static ChaseMonster M(float x = 0, float y = 0, float leash = 100, float reach = 2, float speed = 5)
        => new(Map, x, y, 0, 0, leash, reach, 3, speed);

    private static ChaseVerdict Run(ChaseMonster m, ChaseTarget? t, out int lookups)
    {
        int n = 0;
        var v = ChasePass.Decide(m, 1.0, 0, _ => { n++; return t; });
        lookups = n;
        return v;
    }

    [Fact]
    public void PastLeash_GoesHome_BeforeLookingAtTheTarget()
    {
        var v = Run(M(x: 200), new ChaseTarget(205, 0, false, Map), out int lookups);
        Assert.Equal(new ChaseVerdict(ChaseAction.LeashHome, 0, 0), v);
        Assert.Equal(0, lookups);
    }

    [Theory]
    [InlineData(false, false, false)]   // no target
    [InlineData(true, true, false)]     // dead
    [InlineData(true, false, true)]     // other map
    public void TargetGone_DoesNothing(bool present, bool dead, bool otherMap)
    {
        ChaseTarget? t = present ? new ChaseTarget(10, 0, dead, otherMap ? Map + 1 : Map) : null;
        Assert.Equal(ChaseAction.None, Run(M(), t, out _).Action);
    }

    [Fact]
    public void InReach_DoesNotStep()
        => Assert.Equal(ChaseAction.None, Run(M(), new ChaseTarget(2, 0, false, Map), out _).Action);

    [Fact]
    public void OutOfReach_StepsTowardTarget_ClampedToGapMinusReach()
    {
        // gap 4, reach 2 -> step min(2, speed 5 * dt 1) = 2 -> lands at x=2.
        Assert.Equal(new ChaseVerdict(ChaseAction.Step, 2, 0), Run(M(), new ChaseTarget(4, 0, false, Map), out _));
        // gap 40 -> step min(38, 5) = 5.
        Assert.Equal(new ChaseVerdict(ChaseAction.Step, 5, 0), Run(M(), new ChaseTarget(40, 0, false, Map), out _));
    }

    [Fact]
    public void ZeroAttackReach_FallsBackToFallbackReach()
        => Assert.Equal(ChaseAction.None, Run(M(reach: 0), new ChaseTarget(3, 0, false, Map), out _).Action);

    [Fact]
    public void NoLeashBound_NeverLeashes()
        => Assert.Equal(ChaseAction.Step, Run(M(x: 5000, leash: 0), new ChaseTarget(6000, 0, false, Map), out _).Action);
}
