using RO2.Combats.Domain;
using Xunit;

namespace RO2.Tests.Combat;

/// <summary>Characterization of <see cref="DamageFormulas"/> and <see cref="CastLevelRules"/> (carved from CombatService in B4 S5).</summary>
public class DamageFormulasTests
{
    [Theory]
    // No PCBaseStatus row, no weapon: STR·2 = 100.
    [InlineData(false, 0, 0, 50, 0, 0, 0, 1.0, 100)]
    // PCBaseStatus row, APValue·STR = 100, no weapon.
    [InlineData(true, 2, 0, 50, 0, 0, 0, 1.0, 100)]
    // With a weapon: 100·0.095 + (10+20)/2 = 24.5 -> banker's round to 24.
    [InlineData(true, 2, 0, 50, 0, 10, 20, 1.0, 24)]
    // Skill modifier 3.3 over the raw attack: round(100·3.3) = 330.
    [InlineData(false, 0, 0, 50, 0, 0, 0, 3.3, 330)]
    // Everything zero -> attack floored to 1 -> result 1.
    [InlineData(true, 0, 0, 0, 0, 0, 0, 1.0, 1)]
    public void BaseDamage_MatchesRetail(bool hasPcRow, int ap, int ma, int str, int intel,
        int wMin, int wMax, double skillMod, int expected)
        => Assert.Equal(expected, DamageFormulas.BaseDamage(hasPcRow, ap, ma, str, intel, wMin, wMax, skillMod));

    [Theory]
    [InlineData(10, true, 5, (byte)3, 3)]     // class: min(10,5)=5, then min(5,3)=3
    [InlineData(10, true, 5, null, 5)]        // class, no table cap: min(10,5)=5
    [InlineData(10, false, 0, (byte)11, 10)]  // non-class: learned skipped, min(10,11)=10
    [InlineData(10, true, 0, (byte)5, 0)]     // unlearned class skill clamps to 0
    [InlineData(10, false, 0, null, 10)]      // non-class, no cap: unchanged
    public void CastLevelClamp_MatchesRetail(byte wire, bool isClass, byte learned, byte? limit, byte expected)
        => Assert.Equal(expected, CastLevelRules.Clamp(wire, isClass, learned, limit));
}
