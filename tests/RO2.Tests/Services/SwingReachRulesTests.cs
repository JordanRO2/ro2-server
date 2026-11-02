using RO2.Combats.Domain;
using RO2.Monster.Domain;
using Xunit;

namespace RO2.Tests.Services;

/// <summary>The pure swing reach gate: reach with fallback + tolerance, inclusive Euclidean boundary, non-finite out, victim choice.</summary>
public class SwingReachRulesTests
{
    private const float Tol = 3.890625f;

    [Fact]
    public void SharedConsts_AreTheCurrentOnes()
    {
        Assert.Equal(4f, MeleeReach.MaxAttackRange);
        Assert.Equal(3.890625f, MeleeReach.AttackRangeTolerance);
    }

    [Fact]
    public void InReach_BoundaryInclusive_Euclidean()
    {
        Assert.True(SwingReachRules.InReach(10f, 20f, 2f, 10f + 2f + Tol, 20f));
        Assert.False(SwingReachRules.InReach(10f, 20f, 2f, 10f + 2f + Tol + 0.01f, 20f));
        Assert.True(SwingReachRules.InReach(0f, 0f, 2f, 4f, 3f));   // dist 5 <= 2+3.89
        Assert.False(SwingReachRules.InReach(0f, 0f, 2f, 5f, 5f));  // dist 7.07 > 5.89
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    public void InReach_NonPositiveReach_FallsBackTo4(float reach)
    {
        Assert.True(SwingReachRules.InReach(0f, 0f, reach, 4f + Tol, 0f));
        Assert.False(SwingReachRules.InReach(0f, 0f, reach, 4f + Tol + 0.01f, 0f));
    }

    [Fact]
    public void InReach_NonFinite_IsOut()
    {
        Assert.False(SwingReachRules.InReach(0f, 0f, 50f, float.NaN, 0f));
        Assert.False(SwingReachRules.InReach(0f, 0f, 50f, 0f, float.PositiveInfinity));
        Assert.False(SwingReachRules.InReach(float.NaN, 0f, 50f, 0f, 0f));
    }

    private static SwingCandidate C(bool dead = false, bool sameMap = true, float x = 1f) => new(dead, sameMap, x, 0f);

    [Fact]
    public void Select_AggroInReachWins()
        => Assert.Equal(SwingVictim.Aggro, SwingReachRules.Select(0f, 0f, 2f, C(), C()));

    [Fact]
    public void Select_AggroDeadOtherMapOrFar_FallsToIterating()
    {
        Assert.Equal(SwingVictim.Iterating, SwingReachRules.Select(0f, 0f, 2f, C(dead: true), C()));
        Assert.Equal(SwingVictim.Iterating, SwingReachRules.Select(0f, 0f, 2f, C(sameMap: false), C()));
        Assert.Equal(SwingVictim.Iterating, SwingReachRules.Select(0f, 0f, 2f, C(x: 99f), C()));
    }

    [Fact]
    public void Select_IteratingDead_IsStillSelected_TodaysBehaviour()
        => Assert.Equal(SwingVictim.Iterating, SwingReachRules.Select(0f, 0f, 2f, null, C(dead: true)));

    [Fact]
    public void Select_NullsOrOutOfGate_None()
    {
        Assert.Equal(SwingVictim.None, SwingReachRules.Select(0f, 0f, 2f, null, null));
        Assert.Equal(SwingVictim.None, SwingReachRules.Select(0f, 0f, 2f, null, C(sameMap: false)));
        Assert.Equal(SwingVictim.None, SwingReachRules.Select(0f, 0f, 2f, C(x: 99f), C(x: 99f)));
    }
}
