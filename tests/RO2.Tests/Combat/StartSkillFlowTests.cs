using RO2.Combats.Application;
using Xunit;
using O = RO2.Combats.Application.StartSkillFlow.Outcome;

namespace RO2.Tests.Combat;

/// <summary>Characterization of <see cref="StartSkillFlow.Decide"/> (the 1508 cast-accept branching moved into Combat.Application in B4 S19).</summary>
public class StartSkillFlowTests
{
    [Theory]
    // Not on cooldown -> always Accept, regardless of burst/landed.
    [InlineData(false, false, false, O.Accept)]
    [InlineData(false, true, false, O.Accept)]
    [InlineData(false, true, true, O.Accept)]
    // On cooldown, NOT a burst duplicate -> refuse (landed value irrelevant).
    [InlineData(true, false, false, O.CooldownRefusal)]
    [InlineData(true, false, true, O.CooldownRefusal)]
    // On cooldown, burst duplicate, cast HAS landed -> refuse like any in-cooldown cast.
    [InlineData(true, true, true, O.CooldownRefusal)]
    // On cooldown, burst duplicate, cast NOT landed -> dropped, answered with nothing.
    [InlineData(true, true, false, O.BurstDropNoAnswer)]
    public void Decide_MatchesRetailBranching(bool onCooldown, bool isBurstDuplicate, bool hasLanded, O expected)
        => Assert.Equal(expected, StartSkillFlow.Decide(onCooldown, isBurstDuplicate, hasLanded));
}
