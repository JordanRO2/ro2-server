using Microsoft.Extensions.Logging.Abstractions;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Services;

/// <summary>
/// PcCombatStateService: the player "in battle" stance lifecycle (enter on a combat action, throttled 0x0C
/// re-pulse). The leave timeout is wall-clock driven (DateTime.UtcNow) and is exercised live, not here.
/// </summary>
public class PcCombatStateTests
{
    private sealed class NoDirtySink : IDirtySink
    {
        public void Mark(PlayerState player) { }
        public void Forget(uint characterId) { }
    }

    private static PcCombatStateService NewService()
    {
        var players = new PlayerStateManager(new NoDirtySink());
        var broadcast = new BroadcastService(players, NullLogger<BroadcastService>.Instance);
        return new PcCombatStateService(players, broadcast, NullLogger<PcCombatStateService>.Instance);
    }

    private static PlayerState Pc() => new() { EntityId = 0x00001011, MapId = 1100, MaxHp = 100, Hp = 100 };

    [Fact]
    public void MarkCombat_EntersBattleMode_AndStampsTheAction()
    {
        var svc = NewService();
        var p = Pc();
        Assert.False(p.InBattleMode);

        svc.MarkCombat(p);

        Assert.True(p.InBattleMode);
        Assert.NotEqual(default, p.LastCombatActionAt);
        Assert.NotEqual(default, p.LastBattleStateSentAt);
    }

    [Fact]
    public void MarkCombat_SecondCallWithinThrottle_ReStampsActionButDoesNotRepulse()
    {
        var svc = NewService();
        var p = Pc();
        svc.MarkCombat(p);
        var firstSent = p.LastBattleStateSentAt;

        svc.MarkCombat(p);   // immediate second call: inside the re-pulse interval

        Assert.Equal(firstSent, p.LastBattleStateSentAt);     // the 0x0C was NOT re-pulsed
        Assert.True(p.LastCombatActionAt >= firstSent);        // but the action IS re-stamped (keeps the timeout alive)
        Assert.True(p.InBattleMode);
    }

    [Fact]
    public void MarkCombat_Null_IsNoOp()
    {
        var svc = NewService();
        svc.MarkCombat(null);   // must not throw
    }
}
