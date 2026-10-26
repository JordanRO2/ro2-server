using System;
using RO2.Combats.Domain;
using Xunit;

namespace RO2.Tests.Combat;

/// <summary>Characterization of <see cref="BattleStateRules"/> (the 2398 0x0C battle-stance timing moved out of PcCombatStateService in B4 S15).</summary>
public class BattleStateRulesTests
{
    private static DateTime T(int ms) => new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(ms);

    [Fact]
    public void Windows_ArePinned() // the exact 2398 timing the client depends on
    {
        Assert.Equal(TimeSpan.FromSeconds(5), BattleStateRules.LeaveTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), BattleStateRules.RepulseInterval);
    }

    [Fact]
    public void OnCombatAction_NotInBattle_Enters()
        => Assert.Equal(BattleStateRules.Pulse.Enter, BattleStateRules.OnCombatAction(false, T(0), T(9999)));

    [Fact]
    public void OnCombatAction_InBattle_RepulsesOnlyPastTheInterval()
    {
        Assert.Equal(BattleStateRules.Pulse.None, BattleStateRules.OnCombatAction(true, T(0), T(0)));
        Assert.Equal(BattleStateRules.Pulse.None, BattleStateRules.OnCombatAction(true, T(0), T(1499)));
        Assert.Equal(BattleStateRules.Pulse.Repulse, BattleStateRules.OnCombatAction(true, T(0), T(1500))); // exactly the interval re-pulses (>=)
        Assert.Equal(BattleStateRules.Pulse.Repulse, BattleStateRules.OnCombatAction(true, T(0), T(1501)));
    }

    [Fact]
    public void ShouldLeave_OnlyPastTheTimeout()
    {
        Assert.False(BattleStateRules.ShouldLeave(T(0), T(4999)));
        Assert.True(BattleStateRules.ShouldLeave(T(0), T(5000)));  // exactly the timeout leaves (>=)
        Assert.True(BattleStateRules.ShouldLeave(T(0), T(5001)));
    }
}
