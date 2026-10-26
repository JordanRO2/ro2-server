using System;
using System.Collections.Generic;
using RO2.Combats.Domain;
using Xunit;

namespace RO2.Tests.Combat;

/// <summary>
/// Characterization of <see cref="CooldownRules.EffectiveCooldown"/> (moved verbatim from the host StatService in
/// B4 S1). The result is written into 2715 and enforced from it (send == enforce), so the exact float must not drift.
/// </summary>
public class CooldownRulesTests
{
    [Theory]
    // Vigor 0 -> no reduction, returns the table value.
    [InlineData(10f, 1, 0, 10f)]
    // VigorPct = min(75, 1000/(5+10+10)) = 40 -> 10*(1-0.40) = 6.
    [InlineData(10f, 50, 1000, 6f)]
    // Huge vigor caps VigorPct at 75 -> 10*0.25 = 2.5, the clamp floor.
    [InlineData(10f, 1, 100000, 2.5f)]
    // Non-positive table short-circuits to 0.
    [InlineData(0f, 50, 10, 0f)]
    [InlineData(-5f, 50, 10, 0f)]
    public void EffectiveCooldown_MatchesRetail(float table, int level, int vigor, float expected)
        => Assert.Equal(expected, CooldownRules.EffectiveCooldown(table, level, vigor));

    [Fact]
    public void NegativeVigor_IsClampedToZero_NoReduction()
        => Assert.Equal(10f, CooldownRules.EffectiveCooldown(10f, 20, -50));

    private static DateTime T(int ms) => new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(ms);

    [Fact]
    public void TryEnd_RemovesACoolingGroup_LeavesALapsedOrAbsentOne()
    {
        var cooldowns = new Dictionary<uint, DateTime> { [4101] = T(1000), [4102] = T(500) };

        Assert.True(CooldownRules.TryEnd(cooldowns, 4101, T(0)));   // still cooling -> removed
        Assert.False(cooldowns.ContainsKey(4101));

        Assert.False(CooldownRules.TryEnd(cooldowns, 4102, T(500))); // expiry == now is not cooling (strict >) -> kept
        Assert.True(cooldowns.ContainsKey(4102));

        Assert.False(CooldownRules.TryEnd(cooldowns, 9999, T(0)));   // absent
    }

    [Fact]
    public void TakeResettable_TakesCoolingOtherAcceptedGroups_Ascending()
    {
        var cooldowns = new Dictionary<uint, DateTime>
        {
            [4312] = T(1000),  // the cast's own group — never taken
            [4101] = T(1000),  // cooling + resettable -> taken
            [4121] = T(1000),  // cooling but NOT resettable -> kept
            [2010] = T(100),   // already lapsed at now -> kept
            [3230] = T(1000),  // cooling + resettable -> taken
        };
        var resettable = new HashSet<uint> { 4101, 3230, 4312 };

        var taken = CooldownRules.TakeResettable(cooldowns, 4312, T(500), resettable.Contains);

        Assert.Equal(new uint[] { 3230, 4101 }, taken.ToArray());  // ascending order
        Assert.False(cooldowns.ContainsKey(4101));
        Assert.False(cooldowns.ContainsKey(3230));
        Assert.True(cooldowns.ContainsKey(4312));   // own group stays
        Assert.True(cooldowns.ContainsKey(4121));   // not resettable stays
        Assert.True(cooldowns.ContainsKey(2010));   // lapsed stays
    }
}
