using RO2.Monster.Domain;
using Xunit;

namespace RO2.Tests.Services;

/// <summary>The pure leash-reset transition (<see cref="MonsterLeashRules"/>): clear threat, heal to max, 3 s return window.</summary>
public class MonsterLeashRulesTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ReturnWindow_Is3Seconds() => Assert.Equal(TimeSpan.FromSeconds(3), MonsterLeashRules.ReturnInvulnDuration);

    [Fact]
    public void Reset_ClearsThreatHealsAndStampsWindow()
    {
        var threat = new ThreatTable();
        threat.AddThreat(0x11, "pc", 50);
        var vitals = new MonsterVitals { Hp = 10, MaxHp = 100 };

        var r = MonsterLeashRules.Reset(vitals, threat, new MonsterAiState(), Now);

        Assert.True(r.Changed);
        Assert.Equal(100, r.Vitals.Hp);
        Assert.Empty(threat.Rows);
        Assert.Equal(0u, threat.AggroTargetHandle);
        Assert.Equal(Now.AddSeconds(3), r.Ai.ReturningUntil);
        Assert.True(r.Ai.IsReturning(Now.AddSeconds(3).AddTicks(-1)));
        Assert.False(r.Ai.IsReturning(Now.AddSeconds(3)));
    }

    [Fact]
    public void Reset_NothingToClearAndFull_IsUnchangedButStillStamps()
    {
        var r = MonsterLeashRules.Reset(new MonsterVitals { Hp = 100, MaxHp = 100 }, new ThreatTable(), new MonsterAiState(), Now);

        Assert.False(r.Changed);
        Assert.Equal(Now.AddSeconds(3), r.Ai.ReturningUntil);
    }

    [Fact]
    public void Reset_BelowMaxHpWithNoThreat_IsChanged()
    {
        var r = MonsterLeashRules.Reset(new MonsterVitals { Hp = 99, MaxHp = 100 }, new ThreatTable(), new MonsterAiState(), Now);

        Assert.True(r.Changed);
        Assert.Equal(100, r.Vitals.Hp);
    }

    [Fact]
    public void Reset_KeepsAiConfigAndLifeState()
    {
        var ai = new MonsterAiState { Aggression = NpcAggressionMode.Always, SightRadius = 9f, LeashBound = 30f };
        var r = MonsterLeashRules.Reset(new MonsterVitals { Hp = 5, MaxHp = 100, MaxSp = 7 }, new ThreatTable(), ai, Now);

        Assert.Equal(NpcAggressionMode.Always, r.Ai.Aggression);
        Assert.Equal(30f, r.Ai.LeashBound);
        Assert.False(r.Vitals.IsDead);
        Assert.Equal(7, r.Vitals.MaxSp);
    }
}
