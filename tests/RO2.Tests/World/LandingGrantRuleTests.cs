using System;
using RO2.Worlds.Domain;
using Xunit;

namespace RO2.Tests.World;

/// <summary>Characterization of <see cref="LandingGrantRule"/> (the town-warp landing grant rule in World.Domain, B5 S20).</summary>
public class LandingGrantRuleTests
{
    private static readonly DateTime T = new(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);
    private const uint Skill = WarpDestinationRules.PronteraWarp;

    [Fact]
    public void Window_IsFiveSeconds() => Assert.Equal(TimeSpan.FromSeconds(5), LandingGrantRule.Window);

    [Fact]
    public void SameSkillAndMap_AtTheGrantInstant_CanBeTaken()
        => Assert.True(LandingGrantRule.CanTake(Skill, 1300, T, Skill, 1300, T));

    [Fact]
    public void WindowEdge_IsInclusive_AndOneTickPastIsRefused()
    {
        Assert.True(LandingGrantRule.CanTake(Skill, 1300, T, Skill, 1300, T + LandingGrantRule.Window));
        Assert.False(LandingGrantRule.CanTake(Skill, 1300, T, Skill, 1300, T + LandingGrantRule.Window + TimeSpan.FromTicks(1)));
    }

    [Fact]
    public void ANowBeforeTheGrant_IsRefused()
        => Assert.False(LandingGrantRule.CanTake(Skill, 1300, T, Skill, 1300, T - TimeSpan.FromTicks(1)));

    [Fact]
    public void ADifferentSkillOrMap_IsRefused()
    {
        Assert.False(LandingGrantRule.CanTake(Skill, 1300, T, WarpDestinationRules.MorrocWarp, 1300, T));
        Assert.False(LandingGrantRule.CanTake(Skill, 1300, T, Skill, 1100, T));
    }

    [Fact]
    public void ASpentGrant_SkillIdZero_NeverMatches()
        => Assert.False(LandingGrantRule.CanTake(0, 1300, T, 0, 1300, T));
}
