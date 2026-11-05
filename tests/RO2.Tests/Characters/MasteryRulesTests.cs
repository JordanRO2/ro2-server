using RO2.Characters.Domain;
using Xunit;

namespace RO2.Tests.Characters;

/// <summary>Pure Master-level rules: who may convert, and the per-rank level cap.</summary>
public class MasteryRulesTests
{
    [Theory]
    [InlineData(50, 0, true)]   // at the cap, not started -> may start
    [InlineData(51, 0, true)]   // above the cap counts too
    [InlineData(49, 0, false)]  // below the base cap
    [InlineData(50, 1, false)]  // already started
    [InlineData(50, 7, false)]  // already well into mastery
    public void CanStart_RequiresBaseLevelCapAndNotStarted(int baseLevel, int masteryLevel, bool expected)
        => Assert.Equal(expected, MasteryRules.CanStart((byte)baseLevel, (byte)masteryLevel));

    [Theory]
    [InlineData(0, 10)]
    [InlineData(1, 20)]
    [InlineData(2, 30)]
    [InlineData(3, 40)]
    [InlineData(4, 50)]
    public void LevelCapForRank_IsTenPerRank(int rank, int cap)
        => Assert.Equal(cap, MasteryRules.LevelCapForRank((byte)rank));
}
