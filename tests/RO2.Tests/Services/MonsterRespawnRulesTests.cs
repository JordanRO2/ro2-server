using RO2.Monster.Domain;
using Xunit;

namespace RO2.Tests.Services;

/// <summary>The pure respawn-ready predicate (<see cref="MonsterRespawnRules"/>): 30 s delay, dead and stamped.</summary>
public class MonsterRespawnRulesTests
{
    private static readonly DateTime Died = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly MonsterVitals Dead = new MonsterVitals { Hp = 100, MaxHp = 100 }.Die(Died);

    [Fact]
    public void Delay_Is30Seconds() => Assert.Equal(TimeSpan.FromSeconds(30), MonsterRespawnRules.RespawnDelay);

    [Fact]
    public void Dead_NotReadyBeforeDelay() =>
        Assert.False(MonsterRespawnRules.IsRespawnReady(Dead, Died.AddSeconds(30).AddTicks(-1)));

    [Fact]
    public void Dead_ReadyExactlyAtDelayAndAfter()
    {
        Assert.True(MonsterRespawnRules.IsRespawnReady(Dead, Died.AddSeconds(30)));
        Assert.True(MonsterRespawnRules.IsRespawnReady(Dead, Died.AddMinutes(10)));
    }

    [Fact]
    public void Alive_NeverReady() =>
        Assert.False(MonsterRespawnRules.IsRespawnReady(Dead.Revive(), Died.AddHours(1)));

    [Fact]
    public void DeadWithoutStamp_NeverReady() =>
        Assert.False(MonsterRespawnRules.IsRespawnReady(Dead with { DeathTime = null }, Died.AddHours(1)));
}
