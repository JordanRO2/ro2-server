using System.Collections.Generic;
using RO2.Characters.Domain;
using Xunit;

namespace RO2.Tests.Characters;

/// <summary>
/// Golden pins for the pure stat math moved into Character.Domain (S7): the gear-apply formula, the bonus-stat
/// point curve, and the trait-rate formula. Expected values are captured from the pre-move behaviour.
/// </summary>
public class CharacterStatDomainTests
{
    [Theory]
    [InlineData(1, 100, 107)]   // flat +7 on stat 1
    [InlineData(2, 100, 120)]   // +20% on stat 2
    [InlineData(3, 100, 100)]   // stat 3 untouched
    public void EquipStats_Apply_FlatThenPercent(byte statId, int baseValue, int expected)
    {
        var eq = new EquipStats(
            new Dictionary<byte, int> { [1] = 7 },
            new Dictionary<byte, float> { [2] = 20f });
        Assert.Equal(expected, eq.Apply(statId, baseValue));
    }

    [Fact]
    public void EquipStats_Empty_IsZeroes()
    {
        Assert.Equal(50, EquipStats.Empty.Apply(0, 50));
        Assert.Equal(0, EquipStats.Empty.FlatOf(0));
        Assert.Equal(0f, EquipStats.Empty.PercentOf(0));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(24, 23)]
    [InlineData(25, 25)]
    [InlineData(26, 27)]
    [InlineData(100, 175)]
    public void BonusStatPointsEarned_IsOnePerLevelTo24ThenTwo(int level, int expected)
        => Assert.Equal(expected, CharacterStats.BonusStatPointsEarned(level));

    [Theory]
    [InlineData(0, 50, 0)]
    [InlineData(-5, 50, 0)]
    [InlineData(1000, 1, 6578)]
    [InlineData(1000, 50, 4000)]
    public void TraitRates_Rate_IsHundredCOverDenominator(int c, int level, int expected)
        => Assert.Equal(expected, TraitRates.Rate(c, level));

    [Theory]
    [InlineData((uint)32, true)]
    [InlineData((uint)56, true)]
    [InlineData((uint)95, true)]
    [InlineData((uint)99, false)]
    [InlineData((uint)0, false)]
    public void TraitRates_IsRateTrait(uint traitId, bool expected)
        => Assert.Equal(expected, TraitRates.IsRateTrait(traitId));
}
