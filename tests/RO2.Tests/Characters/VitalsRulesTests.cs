using RO2.Characters.Domain;
using Xunit;

namespace RO2.Tests.Characters;

/// <summary>Characterization of <see cref="VitalsRules"/> regen deltas (moved from host RegenService in B4 S6). float·int then (int) truncation must not drift.</summary>
public class VitalsRulesTests
{
    [Theory]
    [InlineData(10000, 30)]   // 10000 * 0.003f = 30.0 -> 30
    [InlineData(1000, 3)]     // 1000 * 0.003f = 3.0 -> 3
    [InlineData(100, 1)]      // 100 * 0.003f = 0.3 -> (int) 0 -> floored to 1
    [InlineData(0, 1)]        // 0 -> floored to 1
    public void HpRegenDelta_MatchesRetail(int maxHp, int expected)
        => Assert.Equal(expected, VitalsRules.HpRegenDelta(maxHp));

    [Theory]
    [InlineData(10000, 24)]   // 10000 * 0.0024f = 24.0 -> 24
    [InlineData(1000, 2)]     // 1000 * 0.0024f = 2.4 -> (int) 2
    [InlineData(100, 1)]      // 0.24 -> (int) 0 -> floored to 1
    public void SpRegenDelta_MatchesRetail(int maxSp, int expected)
        => Assert.Equal(expected, VitalsRules.SpRegenDelta(maxSp));
}
