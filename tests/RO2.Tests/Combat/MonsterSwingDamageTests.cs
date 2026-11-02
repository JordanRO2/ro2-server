using System;
using RO2.Combats.Domain;
using Xunit;

namespace RO2.Tests.Combat;

/// <summary>
/// Characterization of <see cref="MonsterSwingDamage"/> (carved from NpcCombatService in B6 S11). A seeded roller
/// backed by <see cref="Random"/> reproduces the production draw (<c>pct &gt; 0 &amp;&amp; NextDouble()*100 &lt; pct</c>),
/// and the old inline chain is kept here as the oracle the rule must match draw for draw.
/// </summary>
public class MonsterSwingDamageTests
{
    private sealed class SeededRoller(int seed) : IRoller
    {
        private readonly Random _rng = new(seed);
        public int Draws { get; private set; }
        public bool Roll(double pct)
        {
            if (pct <= 0) return false;
            Draws++;
            return _rng.NextDouble() * 100.0 < pct;
        }
    }

    private sealed class FixedRoller(bool result) : IRoller
    {
        public int Calls { get; private set; }
        public bool Roll(double pct) { Calls++; return result; }
    }

    /// <summary>The pre-carve NpcCombatService chain, verbatim, with the draw taken from <paramref name="rng"/>.</summary>
    private static (int Damage, HitKind Kind) Legacy(int atk, int npcLevel, int defense, int parry, int victimLevel,
        bool evaded, Random rng)
    {
        int def = Math.Max(0, defense);
        double defEff = DefenseRules.PenetratedDef(def, 0, Math.Max(0, npcLevel));
        double defPct = Math.Min(75.0, 100.0 * defEff / (defEff + DefenseRules.DefenseRateAdd + Math.Max(0, npcLevel) * DefenseRules.DefenseRateMulti));
        var damage = Math.Max(1, (int)Math.Round(atk * (1.0 - defPct / 100.0)));
        var kind = HitKind.Normal;
        if (evaded)
        {
            damage = 0;
            kind = HitKind.Miss;
        }
        else if (parry > 0)
        {
            double parryPct = Math.Min(70.0, parry / (0.02 * parry + 40.0 + 0.8 * (victimLevel - Math.Max(0, npcLevel))));
            if (parryPct > 0 && rng.NextDouble() * 100.0 < parryPct)
            {
                damage = Math.Max(1, (int)Math.Round(damage * 0.5));
                kind = HitKind.Guard;
            }
        }
        return (damage, kind);
    }

    [Fact]
    public void Evaded_IsMissZero_AndNeverRolls()
    {
        var r = new FixedRoller(true);
        Assert.Equal((0, HitKind.Miss), MonsterSwingDamage.Resolve(500, 10, 100, 200, 10, true, r));
        Assert.Equal(0, r.Calls);
    }

    [Fact]
    public void NoParryStat_IsNormal_AndNeverRolls()
    {
        var r = new FixedRoller(true);
        // Def 870 vs monster lv 0: 100*870/1740 = 50% -> 1000 * 0.5
        Assert.Equal((500, HitKind.Normal), MonsterSwingDamage.Resolve(1000, 0, 870, 0, 50, false, r));
        Assert.Equal(0, r.Calls);
    }

    [Fact]
    public void ParryLands_IsGuardHalfRoundedToEven_FlooredAtOne()
    {
        // Atk 5, Def 0 -> 5; guard 2.5 -> banker's rounding 2.
        Assert.Equal((2, HitKind.Guard), MonsterSwingDamage.Resolve(5, 0, 0, 100, 10, false, new FixedRoller(true)));
        // Atk 1 -> 1; guard 0.5 -> 0 -> floored at 1.
        Assert.Equal((1, HitKind.Guard), MonsterSwingDamage.Resolve(1, 0, 0, 100, 10, false, new FixedRoller(true)));
    }

    [Fact]
    public void ParryMisses_IsNormalButRolled()
    {
        var r = new FixedRoller(false);
        Assert.Equal((1000, HitKind.Normal), MonsterSwingDamage.Resolve(1000, 0, 0, 100, 10, false, r));
        Assert.Equal(1, r.Calls);
    }

    [Fact]
    public void Mitigation_CapsAt75Percent_AndFloorsAtOne()
    {
        Assert.Equal((250, HitKind.Normal), MonsterSwingDamage.Resolve(1000, 0, 1_000_000, 0, 1, false, new FixedRoller(false)));
        Assert.Equal((1, HitKind.Normal), MonsterSwingDamage.Resolve(0, 5, 100, 0, 1, false, new FixedRoller(false)));
    }

    [Fact]
    public void NegativeInputs_AreClampedToZero()
    {
        Assert.Equal((1000, HitKind.Normal), MonsterSwingDamage.Resolve(1000, -5, -100, 0, 1, false, new FixedRoller(false)));
    }

    [Fact]
    public void Seeded_MatchesTheLegacyInlineChain_DrawForDraw()
    {
        var gen = new Random(12345);
        foreach (var seed in new[] { 1, 7, 42 })
        {
            var roller = new SeededRoller(seed);
            var legacyRng = new Random(seed);
            for (int i = 0; i < 2000; i++)
            {
                int atk = gen.Next(0, 5000), npcLv = gen.Next(-2, 120), def = gen.Next(-50, 20000);
                int parry = gen.Next(0, 3) == 0 ? 0 : gen.Next(0, 3000), vLv = gen.Next(1, 130);
                bool evaded = gen.Next(0, 10) == 0;
                Assert.Equal(Legacy(atk, npcLv, def, parry, vLv, evaded, legacyRng),
                    MonsterSwingDamage.Resolve(atk, npcLv, def, parry, vLv, evaded, roller));
            }
            // Both consumed the identical number of draws: the next draw from each stream agrees.
            Assert.Equal(legacyRng.NextDouble(), new Func<double>(() => { var r2 = new Random(seed); for (int k = 0; k < roller.Draws; k++) r2.NextDouble(); return r2.NextDouble(); })());
        }
    }
}
