using RO2.Combats.Domain;
using Xunit;
using V = RO2.Combats.Domain.SwingVerdict.Verdict;

namespace RO2.Tests.Combat;

/// <summary>Characterization of <see cref="SwingVerdict.ForTarget"/> (the 1498 normal-attack target gate moved into Combat.Domain in B4 S24).</summary>
public class SwingVerdictTests
{
    [Fact]
    public void Unresolved_NotKnownActor()
        => Assert.Equal(V.NotKnownActor, SwingVerdict.ForTarget(npcResolved: false, npcIsDead: true, npcCamp: 0));

    [Fact]
    public void Dead_BeforeHostileCheck()
        => Assert.Equal(V.TargetDead, SwingVerdict.ForTarget(true, npcIsDead: true, npcCamp: 2));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void FriendlyCamp_NotHostile(int camp)
        => Assert.Equal(V.NotHostile, SwingVerdict.ForTarget(true, false, camp));

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void LiveHostileMonster_Grant(int camp)
        => Assert.Equal(V.Grant, SwingVerdict.ForTarget(true, false, camp));
}
