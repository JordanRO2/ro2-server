using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Validation;

public class CastStateTests
{
    private static readonly DateTime T0 = new(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Life = TimeSpan.FromSeconds(5);

    private static PlayerState Caster() => new() { MapId = 1100 };

    [Fact]
    public void RecordedCastIsConsumedOnce()
    {
        var p = Caster();
        SkillCastLandings.RecordPending(p, 412105, 0x50000001u, T0, Life);

        Assert.True(SkillCastLandings.TryConsume(p, 412105, T0.AddMilliseconds(300), out var cast));
        Assert.Equal(412105u, cast.SkillId);
        Assert.Equal(4121u, cast.Group);
        Assert.Equal(5u, cast.Level);
        Assert.Equal(0x50000001u, cast.TargetHandle);
        Assert.Equal(T0, cast.AcceptedAtUtc);
        Assert.False(SkillCastLandings.TryConsume(p, 412105, T0.AddMilliseconds(400), out _));
    }

    [Fact]
    public void ForgedIdWithNothingPendingFails()
    {
        var p = Caster();
        Assert.False(SkillCastLandings.TryConsume(p, 412105, T0, out _));
        SkillCastLandings.RecordPending(p, 412105, 1, T0, Life);
        Assert.True(SkillCastLandings.TryConsume(p, 412105, T0, out _));
        Assert.False(SkillCastLandings.TryConsume(p, 412105, T0, out _));
    }

    [Fact]
    public void NonMatchingIdOrTargetFailsAndKeepsTheEntry()
    {
        var p = Caster();
        SkillCastLandings.RecordPending(p, 412105, 7, T0, Life);

        Assert.False(SkillCastLandings.TryConsume(p, 412106, T0, out _));   
        Assert.False(SkillCastLandings.TryConsume(p, 999901, T0, out _));
        Assert.False(SkillCastLandings.TryConsume(p, 412105, T0, out _, targetHandle: 8));
        Assert.False(SkillCastLandings.TryConsumeGroup(p, 4122, T0, out _));
        Assert.Equal(1, SkillCastLandings.PendingCount(p, T0));
        Assert.True(SkillCastLandings.TryConsume(p, 412105, T0, out _, targetHandle: 7));
    }

    [Fact]
    public void GroupConsumeMatchesAnyLevelOnce()
    {
        var p = Caster();
        SkillCastLandings.RecordPending(p, 412105, 7, T0, Life);
        Assert.True(SkillCastLandings.TryConsumeGroup(p, 4121, T0, out var cast));
        Assert.Equal(412105u, cast.SkillId);
        Assert.False(SkillCastLandings.TryConsumeGroup(p, 4121, T0, out _));
    }

    [Fact]
    public void TwoAcceptedCastsAreTwoConsumes()
    {
        var p = Caster();
        SkillCastLandings.RecordPending(p, 412105, 7, T0, Life);
        SkillCastLandings.RecordPending(p, 412105, 7, T0.AddSeconds(1), Life);
        Assert.True(SkillCastLandings.TryConsume(p, 412105, T0.AddSeconds(2), out var first));
        Assert.Equal(T0, first.AcceptedAtUtc);                     
        Assert.True(SkillCastLandings.TryConsume(p, 412105, T0.AddSeconds(2), out _));
        Assert.False(SkillCastLandings.TryConsume(p, 412105, T0.AddSeconds(2), out _));
    }

    [Fact]
    public void ExpiredEntryIsNotConsumableAndIsPruned()
    {
        var p = Caster();
        SkillCastLandings.RecordPending(p, 412105, 7, T0, Life);

        Assert.False(SkillCastLandings.TryConsume(p, 412105, T0 + Life, out _));        
        Assert.Equal(0, SkillCastLandings.PendingCount(p, T0 + Life));

        SkillCastLandings.RecordPending(p, 412105, 7, T0, Life);
        Assert.Equal(1, SkillCastLandings.PruneExpired(p, T0.AddSeconds(6)));
        Assert.Equal(0, SkillCastLandings.PruneExpired(p, T0.AddSeconds(6)));
    }

    [Fact]
    public void DefaultLifetimeIsCastDurationPlusGrace()
    {
        var p = Caster();
        SkillCastLandings.RecordPending(p, 412105, 7, T0);
        var expected = SkillCasterBusyWindow.CastDuration(412105) + SkillCastLandings.PendingGrace;
        Assert.True(SkillCastLandings.TryConsume(p, 412105, T0 + expected - TimeSpan.FromMilliseconds(1), out var cast));
        Assert.Equal(T0 + expected, cast.ExpiresAtUtc);
    }

    [Fact]
    public void PendingListIsCappedPerPlayer()
    {
        var p = Caster();
        for (var i = 0; i < SkillCastLandings.MaxPendingPerPlayer + 10; i++)
            SkillCastLandings.RecordPending(p, 412105, (uint)i, T0, Life);
        Assert.Equal(SkillCastLandings.MaxPendingPerPlayer, SkillCastLandings.PendingCount(p, T0));
    }

    [Fact]
    public void PlayersDoNotShareEntries()
    {
        var a = Caster();
        var b = Caster();
        SkillCastLandings.RecordPending(a, 412105, 7, T0, Life);
        Assert.False(SkillCastLandings.TryConsume(b, 412105, T0, out _));
        Assert.True(SkillCastLandings.TryConsume(a, 412105, T0, out _));
    }

    [Fact]
    public void ConcurrentConsumesNeverDoubleConsume()
    {
        for (var round = 0; round < 50; round++)
        {
            var p = Caster();
            SkillCastLandings.RecordPending(p, 412105, 7, T0, Life);
            var wins = 0;
            Parallel.For(0, 64, new ParallelOptions { MaxDegreeOfParallelism = 16 }, i =>
            {
                if (SkillCastLandings.TryConsume(p, 412105, T0, out _)) Interlocked.Increment(ref wins);
            });
            Assert.Equal(1, wins);
        }
    }

    [Fact]
    public void ConcurrentConsumesOfNEntriesYieldExactlyN()
    {
        var p = Caster();
        for (var i = 0; i < 5; i++) SkillCastLandings.RecordPending(p, 412105, 7, T0, Life);
        var wins = 0;
        Parallel.For(0, 200, new ParallelOptions { MaxDegreeOfParallelism = 16 }, i =>
        {
            if (SkillCastLandings.TryConsume(p, 412105, T0, out _)) Interlocked.Increment(ref wins);
        });
        Assert.Equal(5, wins);
    }

    
    [Fact]
    public void GovernFailsWithNothingPending()
        => Assert.False(SkillCastLandings.TryGovern(Caster(), 412105, T0, consume: true, out _));

    [Fact]
    public void GovernConsumesANonChannelledCastExactlyOnce()
    {
        var p = Caster();
        SkillCastLandings.RecordPending(p, 412105, 0x50000001u, T0, Life);

        Assert.True(SkillCastLandings.TryGovern(p, 412105, T0.AddMilliseconds(200), consume: true, out var cast));
        Assert.Equal(412105u, cast.SkillId);
        Assert.Equal(0x50000001u, cast.TargetHandle);
        
        Assert.False(SkillCastLandings.TryGovern(p, 412105, T0.AddMilliseconds(300), consume: true, out _));
    }

    [Fact]
    public void GovernDoesNotConsumeWhenChannelledSoRepeated1509sAndTicksPass()
    {
        var p = Caster();
        SkillCastLandings.RecordPending(p, 630410, 7, T0, Life);
        
        Assert.True(SkillCastLandings.TryGovern(p, 630410, T0.AddSeconds(1), consume: false, out _));
        Assert.True(SkillCastLandings.TryGovern(p, 630410, T0.AddSeconds(2), consume: false, out _));
        Assert.True(SkillCastLandings.TryGovern(p, 630410, T0.AddSeconds(3), consume: false, out _));
        Assert.Equal(1, SkillCastLandings.PendingCount(p, T0.AddSeconds(3)));
        
        Assert.False(SkillCastLandings.TryGovern(p, 630410, T0 + Life, consume: false, out _));
    }

    [Fact]
    public void GovernRefusesAForgedDifferentSkillAndCannotInflateTheRate()
    {
        
        
        var p = Caster();
        SkillCastLandings.RecordPending(p, 412105, 7, T0, Life);
        Assert.False(SkillCastLandings.TryGovern(p, 330101, T0, consume: true, out _));
        
        
        Assert.True(SkillCastLandings.TryGovern(p, 412105, T0, consume: true, out var cast));
        Assert.Equal(412105u, cast.SkillId);
    }

    [Fact]
    public void GovernRefusesAfterTheGraceExpires()
    {
        var p = Caster();
        SkillCastLandings.RecordPending(p, 412105, 7, T0, Life);
        Assert.False(SkillCastLandings.TryGovern(p, 412105, T0 + Life, consume: true, out _));
    }

    [Fact]
    public void ExistingInFlightGroupBookkeepingIsUntouched()
    {
        var p = Caster();
        SkillCastLandings.RecordPending(p, 412105, 7, T0, Life);
        Assert.True(SkillCastLandings.HasLanded(p, 4121));          
        SkillCastLandings.BeginCast(p, 4121);
        Assert.False(SkillCastLandings.HasLanded(p, 4121));
        Assert.True(SkillCastLandings.TryConsume(p, 412105, T0, out _));
        Assert.False(SkillCastLandings.HasLanded(p, 4121));         
        SkillCastLandings.MarkLanded(p, 412105);
        Assert.True(SkillCastLandings.HasLanded(p, 4121));
    }
}
