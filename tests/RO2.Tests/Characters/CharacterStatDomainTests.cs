using System;
using System.Collections.Generic;
using System.Linq;
using RO2.Characters.Domain;
using RO2.GameData.Tables;
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

    // PvP Damage Increase/Reduction (trait 71/72) diminishing curve 100*raw/(raw+5500): pinned to the operator's
    // "CLS Formula" table rows, including the exact 45% crossover at raw 4500 and the 100%-from-below asymptote.
    [Theory]
    [InlineData(0f, 0.0)]
    [InlineData(300f, 5.1724)]
    [InlineData(900f, 14.0625)]
    [InlineData(1500f, 21.4286)]
    [InlineData(4500f, 45.0)]       // crossover: equals the old linear raw/100
    [InlineData(9600f, 63.5762)]
    [InlineData(11400f, 67.4556)]
    public void PvpDamageRatePct_MatchesTheClsCurve(float rawSum, double expectedPct) =>
        Assert.Equal(expectedPct, CharacterStats.PvpDamageRatePct(rawSum), 3);

    [Fact]
    public void PvpDamageRatePct_AsymptotesBelow100_NeverReachesIt()
    {
        Assert.True(CharacterStats.PvpDamageRatePct(1_000_000f) < 100f);
        Assert.True(CharacterStats.PvpDamageRatePct(1_000_000f) > 99f);
    }

    // General damage increase (trait 75) / reduction (trait 51 DamageDown) are read straight off the gear's percent
    // map into EffectiveStats: PercentOf already divided each line by 100, so a +500 line is +5% and a -1000 line -10%.
    [Fact]
    public void EffectivePlayerStats_CarriesGeneralDamageRates()
    {
        var eq = new EquipStats(
            new Dictionary<byte, int>(),
            new Dictionary<byte, float> { [75] = 5f, [51] = -10f });
        var s = CharacterStats.EffectivePlayerStats(new Character(), eq, null, Array.Empty<NpcLevelScaling>());
        Assert.Equal(5f, s.DamageIncrease);
        Assert.Equal(-10f, s.DamageReduction);
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

    // ---- S8: row-param stat math ----

    [Fact]
    public void ComputeBaseStats_NullRow_IsAllZero()
    {
        var bs = CharacterStats.ComputeBaseStats(null, Curve, 10, 1, 1, 1, 1, 1, 1);
        Assert.Equal(0u, bs.MaxHp);
        Assert.Equal((ushort)0, bs.MaxSp);
        Assert.Empty(bs.Derived);
    }

    private static readonly NpcLevelScaling[] Curve = Enumerable.Range(1, 50)
        .Select(l => new NpcLevelScaling { Level = (byte)l, Base_HP = (ushort)Math.Floor(212.5 + 153.0 * (l - 1) / 3.92) }).ToArray();

    // Retail oracle: naked class-4 Lv50, VIT 17 => HP 2329 / SP 90 (Base_HP(50)=2125 + VitHPValue 12 * 17).
    [Fact]
    public void ComputeBaseStats_Lv50NakedClass4_MatchesRetail()
    {
        var pc = new PCBaseStatus { VitHPValue = 12, WisSPValue = 15 };
        var bs = CharacterStats.ComputeBaseStats(pc, Curve, 50, 1, 1, 1, 6, 17, 1);
        Assert.Equal(2329u, bs.MaxHp);
        Assert.Equal((ushort)90, bs.MaxSp);
    }

    [Fact]
    public void LevelBaseHp_ExtrapolatesLinearlyPastTheTable()
        => Assert.Equal(2125 + 2 * (2125 - Curve[48].Base_HP), CharacterStats.LevelBaseHp(Curve, 52));

    [Fact]
    public void ComputeBaseStats_AppliesHpFloorAndPrimaryCoefficients()
    {
        var pc = new PCBaseStatus
        {
            BaseHP = 1000f, BaseHPRate = 0.85f, BaseHPRateValue = 0.25f, BaseHPValue = 50f,
            VitHPValue = 12, WisSPValue = 15,
            APValue = 2, MAValue = 3, VitDefenseValue = 4, AgiCrticalValue = 5,
            StrParryValue = 6, IntParryValue = 7, AgiDodgeValue = 8, StrHitValue = 9, IntHitValue = 10,
        };
        var bs = CharacterStats.ComputeBaseStats(pc, Curve, level: 10, str: 100, agi: 50, intel: 30, wis: 20, vit: 40, luk: 5);
        Assert.Equal(1043u, bs.MaxHp);        // Base_HP(10)=563 + 12*40
        Assert.Equal((ushort)300, bs.MaxSp);  // 15*20
        Assert.Equal(200, bs.Derived[23]);    // AP   = 2*100
        Assert.Equal(90, bs.Derived[25]);     // MA   = 3*30
        Assert.Equal(160, bs.Derived[26]);    // Def  = 4*40
        Assert.Equal(250, bs.Derived[60]);    // Crit = 5*50
        Assert.Equal(810, bs.Derived[61]);    // Parry= 6*100 + 7*30
        Assert.Equal(400, bs.Derived[59]);    // Dodge= 8*50
        Assert.Equal(1200, bs.Derived[58]);   // Hit  = 9*100 + 10*30
    }

    [Fact]
    public void ClassBasePrimaries_MapsWisFromBaseDex()
    {
        var s = new StandardStat { Base_STR = 5, Base_AGI = 6, Base_INT = 7, Base_DEX = 8, Base_VIT = 9, Base_LUK = 10 };
        Assert.Equal((5, 6, 7, 8, 9, 10), CharacterStats.ClassBasePrimaries(s)!.Value);
        Assert.Null(CharacterStats.ClassBasePrimaries(null));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(50, 2)]
    [InlineData(999, 0)]
    [InlineData(500, 1)]   // off-table => default 1
    public void BonusStatCost_LooksUpOrDefaultsToOne(int current, int expected)
    {
        var map = new Dictionary<int, byte> { [1] = 1, [50] = 2, [999] = 0 };
        Assert.Equal(expected, CharacterStats.BonusStatCost(map, current));
    }

    [Fact]
    public void ResetBonusStats_ZeroesBonusesAndSetsBasePrimaries()
    {
        var c = new Character { BonusStr = 5, BonusVit = 3, Str = 99, Vit = 88 };
        Assert.True(CharacterStats.ResetBonusStats(c, (1, 2, 3, 4, 5, 6)));
        Assert.Equal(0, c.BonusStr);
        Assert.Equal(0, c.BonusVit);
        Assert.Equal(1, c.Str);
        Assert.Equal(4, c.Wis);
        Assert.Equal(6, c.Luk);
    }

    [Fact]
    public void ResetBonusStats_NullPrimaries_ReturnsFalseAndChangesNothing()
    {
        var c = new Character { BonusStr = 5, Str = 99 };
        Assert.False(CharacterStats.ResetBonusStats(c, null));
        Assert.Equal(5, c.BonusStr);
        Assert.Equal(99, c.Str);
    }

    // ---- S9: gear-fold ----

    [Fact]
    public void EquipStats_Fold_SplitsFlatAndPercentByKind()
    {
        var eq = EquipStats.Fold(new[]
        {
            new TraitLine(1, 0, 5f),       // flat
            new TraitLine(1, 0, 3f),       // flat, same stat => 8
            new TraitLine(2, 26, 1000f),   // percent => 10
            new TraitLine(2, 26, 500f),    // percent, same stat => 15
            new TraitLine(1, 96, 10.6f),   // flat, rounds to 11
            new TraitLine(3, 7, 2f),       // non-1/2 kind treated as flat
        });
        Assert.Equal(8, eq.FlatOf(0));
        Assert.Equal(15f, eq.PercentOf(26));
        Assert.Equal(11, eq.FlatOf(96));
        Assert.Equal(2, eq.FlatOf(7));
    }

    [Fact]
    public void EquipStats_WithLines_FoldsOntoABase()
    {
        var baseStats = new EquipStats(
            new Dictionary<byte, int> { [0] = 10 },
            new Dictionary<byte, float> { [26] = 5f });
        var eq = EquipStats.WithLines(baseStats, new[] { new TraitLine(1, 0, 3f), new TraitLine(2, 26, 500f) });
        Assert.Equal(13, eq.FlatOf(0));
        Assert.Equal(10f, eq.PercentOf(26));
    }
}
