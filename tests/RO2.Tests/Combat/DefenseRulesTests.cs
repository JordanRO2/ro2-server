using RO2.Combats.Domain;
using Xunit;

namespace RO2.Tests.Combat;

/// <summary>
/// Characterization of <see cref="DefenseRules"/> (PenetratedDef + Mitigate, moved verbatim from the host in B4 S2).
/// These feed the damage numbers broadcast in 2718/2696, so the arithmetic must not drift.
/// </summary>
public class DefenseRulesTests
{
    [Theory]
    [InlineData(100, 0, 50, 100.0)]        // Pen 0 -> strict no-op (armour unchanged)
    [InlineData(0, 50, 50, 0.0)]           // Def 0 -> 0
    [InlineData(100, 1000, 50, 60.0)]      // PenPct = min(50, 1000/25) = 40 -> 100*0.60
    [InlineData(100, 1_000_000, 1, 50.0)]  // PenPct capped at 50 -> 100*0.50
    public void PenetratedDef_MatchesRetail(int def, int pen, int attackerLevel, double expected)
        => Assert.Equal(expected, DefenseRules.PenetratedDef(def, pen, attackerLevel));

    [Theory]
    [InlineData(1000, 870, 0, 0, 500)]         // 870/(870+870) = 50%
    [InlineData(1000, 1_000_000, 0, 0, 250)]   // capped at 75%
    [InlineData(1, 1_000_000, 0, 0, 1)]        // never below 1
    public void Mitigate_MatchesRetail(int raw, int def, int pen, int attackerLevel, int expected)
        => Assert.Equal(expected, DefenseRules.Mitigate(raw, def, pen, attackerLevel));
}
