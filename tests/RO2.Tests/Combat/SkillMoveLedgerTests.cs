using System;
using RO2.Combats.Domain;
using Xunit;

namespace RO2.Tests.Combat;

/// <summary>Characterization of <see cref="SkillMoveLedger"/> (the ground-object move ledgers moved out of the host in B4 S13).</summary>
public class SkillMoveLedgerTests
{
    private static DateTime T(int ms) => new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(ms);

    [Fact]
    public void IsTargetMoveKind_OnlyTheFiveKinds()
    {
        foreach (byte k in new byte[] { 9, 14, 17, 20, 22 }) Assert.True(SkillMoveLedger.IsTargetMoveKind(k));
        foreach (byte k in new byte[] { 0, 8, 10, 15, 16, 18, 19, 21, 23 }) Assert.False(SkillMoveLedger.IsTargetMoveKind(k));
    }

    [Fact]
    public void Cast_TargetAndSkillChecks_ByMapAndSkill()
    {
        var l = new SkillMoveLedger();
        Assert.False(l.IsCastSkill(1300, 630401));                  // no cast yet
        l.RecordCast(1300, 630401, 0x00205D13u, T(0));
        Assert.True(l.IsCastSkill(1300, 630401));
        Assert.True(l.IsCastTarget(1300, 630401, 0x00205D13u));
        Assert.False(l.IsCastTarget(1300, 630401, 0u));             // a zero handle never is
        Assert.False(l.IsCastTarget(1100, 630401, 0x00205D13u));    // other map
        Assert.False(l.IsCastSkill(1300, 630402));                  // other skill

        l.RecordCast(1300, 630402, 0u, T(1));                       // another skill's cast does NOT evict it
        Assert.True(l.IsCastSkill(1300, 630401));
        Assert.True(l.IsCastSkill(1300, 630402));
    }

    [Fact]
    public void Cast_EvictsTheOldestPastTheCap()
    {
        var l = new SkillMoveLedger();
        for (uint i = 0; i < SkillMoveLedger.MaxCastsPerCaster + 5; i++)
            l.RecordCast(1300, 800000 + i, 1u, T((int)i));
        Assert.False(l.IsCastSkill(1300, 800000));                                                   // oldest evicted
        Assert.True(l.IsCastSkill(1300, 800000 + (uint)SkillMoveLedger.MaxCastsPerCaster + 4));      // newest kept
    }

    [Fact]
    public void Move_RecordReplaces_TryTakeConsumes()
    {
        var l = new SkillMoveLedger();
        Assert.True(l.Record(630410, new SkillMoveLedger.SkillMove(0x00205D13u, 0x0E, 1f, 2f, 3f)));
        Assert.True(l.Record(630410, new SkillMoveLedger.SkillMove(0x00205D13u, 0x0E, 4f, 5f, 6f))); // a later step replaces it
        Assert.False(l.TryTake(630405, out _));                                                      // another skill
        Assert.True(l.TryTake(630410, out var move));
        Assert.Equal(new SkillMoveLedger.SkillMove(0x00205D13u, 0x0E, 4f, 5f, 6f), move);
        Assert.False(l.TryTake(630410, out _));                                                      // already taken
    }

    [Fact]
    public void Move_RefusesANewOnePastTheCap_StillReplacesAHeldOne()
    {
        var l = new SkillMoveLedger();
        for (uint i = 0; i < SkillMoveLedger.MaxMovesPerCaster; i++)
            l.Record(700000 + i, new SkillMoveLedger.SkillMove(1, 9, 0f, 0f, 0f));
        Assert.Equal(SkillMoveLedger.MaxMovesPerCaster, l.MoveCount());

        Assert.True(l.Record(700000, new SkillMoveLedger.SkillMove(2, 9, 1f, 1f, 1f)));  // replacing a held skill is allowed
        Assert.False(l.Record(999999, new SkillMoveLedger.SkillMove(3, 9, 0f, 0f, 0f))); // a new one is refused at the cap

        Assert.True(l.TryTake(700000, out _));
        Assert.True(l.Record(999999, new SkillMoveLedger.SkillMove(3, 9, 0f, 0f, 0f)));  // room again after a take
    }
}
