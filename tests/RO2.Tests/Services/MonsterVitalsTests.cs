using RO2.Monster.Domain;
using Xunit;

namespace RO2.Tests.Services;

/// <summary>The pure monster HP transitions (<see cref="MonsterVitals"/>): damage clamp, death, heal, leash restore, revive.</summary>
public class MonsterVitalsTests
{
    private static readonly MonsterVitals Full = new() { Hp = 100, MaxHp = 100, MaxSp = 7 };

    [Fact]
    public void ApplyDamage_ClampsToHpLeftAndDoesNotMarkDeath()
    {
        var (v, actual) = Full.ApplyDamage(250);
        Assert.Equal(100, actual);
        Assert.Equal(0, v.Hp);
        Assert.True(v.IsOutOfHp);
        Assert.False(v.IsDead);
    }

    [Fact]
    public void ApplyDamage_PartialHit_KeepsAlive()
    {
        var (v, actual) = Full.ApplyDamage(30);
        Assert.Equal(30, actual);
        Assert.Equal(70, v.Hp);
        Assert.False(v.IsOutOfHp);
    }

    [Fact]
    public void Die_SetsZeroHpDeadAndStamp()
    {
        var now = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var v = Full.Die(now);
        Assert.Equal((0, true, now), (v.Hp, v.IsDead, v.DeathTime!.Value));
    }

    [Fact]
    public void Heal_ClampsToMaxHp_AndNeverRaisesACorpse()
    {
        Assert.Equal(100, (Full with { Hp = 90 }).Heal(50).Hp);
        var corpse = Full.Die(DateTime.UtcNow);
        Assert.Same(corpse, corpse.Heal(50));
    }

    [Fact]
    public void RestoreHp_KeepsLifeState_Revive_ResetsIt()
    {
        var hurt = Full with { Hp = 5 };
        Assert.Equal(100, hurt.RestoreHp().Hp);
        var r = Full.Die(DateTime.UtcNow).Revive();
        Assert.Equal((100, false, (DateTime?)null), (r.Hp, r.IsDead, r.DeathTime));
        Assert.Equal(7, r.MaxSp);
    }
}
