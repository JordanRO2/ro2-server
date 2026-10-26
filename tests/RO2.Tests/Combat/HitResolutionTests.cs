using System.Collections.Generic;
using RO2.Combats.Domain;
using Xunit;

namespace RO2.Tests.Combat;

/// <summary>
/// Characterization of <see cref="HitResolution"/> (carved from CombatService in B4 S4) with a scripted roller.
/// Pins the damage/kind outcomes AND the roll order: PvP rolls crit first and parry only if crit missed; PvE
/// rolls crit only.
/// </summary>
public class HitResolutionTests
{
    /// <summary>A roller that returns queued results and records how many times it was asked.</summary>
    private sealed class ScriptedRoller(params bool[] results) : IRoller
    {
        private readonly Queue<bool> _results = new(results);
        public int Calls { get; private set; }
        public bool Roll(double pct) { Calls++; return _results.Count > 0 && _results.Dequeue(); }
    }

    [Fact]
    public void Pve_NoCrit_IsNormalFlooredAtOne()
    {
        var r = new ScriptedRoller(false);
        Assert.Equal((500, HitKind.Normal), HitResolution.ResolvePveHit(500, 0, 10, 10, r));
        Assert.Equal((1, HitKind.Normal), HitResolution.ResolvePveHit(0, 0, 10, 10, new ScriptedRoller(false)));
    }

    [Fact]
    public void Pve_Crit_DoublesAndFlagsCrit()
        => Assert.Equal((1000, HitKind.Crit), HitResolution.ResolvePveHit(500, 50, 10, 10, new ScriptedRoller(true)));

    [Fact]
    public void Pvp_Crit_WinsAndParryIsNotRolled()
    {
        var r = new ScriptedRoller(true);                 // crit lands
        Assert.Equal((1000, HitKind.Crit), HitResolution.ResolvePvpHit(1000, 870, 0, 0, 0, 50, 50, r));
        Assert.Equal(1, r.Calls);                         // parry never rolled
    }

    [Fact]
    public void Pvp_CritMiss_ThenParry_IsGuardHalfRounded()
    {
        var r = new ScriptedRoller(false, true);          // crit miss, parry lands
        Assert.Equal((250, HitKind.Guard), HitResolution.ResolvePvpHit(1000, 870, 0, 0, 0, 50, 50, r));
        Assert.Equal(2, r.Calls);
    }

    [Fact]
    public void Pvp_BothMiss_IsNormalMitigated()
    {
        var r = new ScriptedRoller(false, false);
        Assert.Equal((500, HitKind.Normal), HitResolution.ResolvePvpHit(1000, 870, 0, 0, 0, 50, 50, r));
        Assert.Equal(2, r.Calls);
    }
}
