using RO2.Combats.Domain;
using Xunit;

namespace RO2.Tests.Combat;

/// <summary>
/// Characterization of <see cref="CooldownRules.EffectiveCooldown"/> (moved verbatim from the host StatService in
/// B4 S1). The result is written into 2715 and enforced from it (send == enforce), so the exact float must not drift.
/// </summary>
public class CooldownRulesTests
{
    [Theory]
    // Vigor 0 -> no reduction, returns the table value.
    [InlineData(10f, 1, 0, 10f)]
    // VigorPct = min(75, 1000/(5+10+10)) = 40 -> 10*(1-0.40) = 6.
    [InlineData(10f, 50, 1000, 6f)]
    // Huge vigor caps VigorPct at 75 -> 10*0.25 = 2.5, the clamp floor.
    [InlineData(10f, 1, 100000, 2.5f)]
    // Non-positive table short-circuits to 0.
    [InlineData(0f, 50, 10, 0f)]
    [InlineData(-5f, 50, 10, 0f)]
    public void EffectiveCooldown_MatchesRetail(float table, int level, int vigor, float expected)
        => Assert.Equal(expected, CooldownRules.EffectiveCooldown(table, level, vigor));

    [Fact]
    public void NegativeVigor_IsClampedToZero_NoReduction()
        => Assert.Equal(10f, CooldownRules.EffectiveCooldown(10f, 20, -50));
}
