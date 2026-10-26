using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Combats.Domain;
using Xunit;

namespace RO2.Tests.Validation;

/// <summary>MV-4: the self-move cast check and the per-caster cap on held moves.</summary>
public class SkillMoveTrackerCapTests
{
    [Fact]
    public void IsCastSkill_IsPerSkillAndIgnoresTarget()
    {
        var caster = new PlayerState();
        Assert.False(SkillMoveTracker.IsCastSkill(caster, 1300, 630401)); // no cast yet

        SkillMoveTracker.RecordCast(caster, 1300, 630401, 0x00205D13u);
        Assert.True(SkillMoveTracker.IsCastSkill(caster, 1300, 630401));
        Assert.False(SkillMoveTracker.IsCastSkill(caster, 1300, 630402)); // other skill
        Assert.False(SkillMoveTracker.IsCastSkill(caster, 1100, 630401)); // other map

        SkillMoveTracker.RecordCast(caster, 1300, 630402, 0u); // another skill's cast does NOT evict it
        Assert.True(SkillMoveTracker.IsCastSkill(caster, 1300, 630401));
        Assert.True(SkillMoveTracker.IsCastSkill(caster, 1300, 630402));
    }

    [Fact]
    public void RecordCast_IsCappedPerCasterEvictingTheOldest()
    {
        var caster = new PlayerState();
        for (uint i = 0; i < SkillMoveTracker.MaxCastsPerCaster + 5; i++)
        {
            SkillMoveTracker.RecordCast(caster, 1300, 800000 + i, 1u);
            Thread.Sleep(1); // distinct AtUtc so "oldest" is well defined
        }
        Assert.False(SkillMoveTracker.IsCastSkill(caster, 1300, 800000));       // oldest evicted
        Assert.True(SkillMoveTracker.IsCastSkill(caster, 1300, 800000 + (uint)SkillMoveTracker.MaxCastsPerCaster + 4)); // newest kept
    }

    [Fact]
    public void Record_IsCappedPerCasterAndStillReplacesAHeldSkill()
    {
        var caster = new PlayerState();
        Assert.Equal(0, SkillMoveTracker.MoveCount(caster));
        for (uint i = 0; i < 10_000; i++)
            SkillMoveTracker.Record(caster, 700000 + i, new SkillMoveLedger.SkillMove(1, 9, 0f, 0f, 0f));
        Assert.Equal(SkillMoveTracker.MaxMovesPerCaster, SkillMoveTracker.MoveCount(caster));

        // A held skill is still replaced at the cap; a new one is refused.
        Assert.True(SkillMoveTracker.Record(caster, 700000, new SkillMoveLedger.SkillMove(2, 9, 1f, 1f, 1f)));
        Assert.False(SkillMoveTracker.Record(caster, 999999, new SkillMoveLedger.SkillMove(3, 9, 0f, 0f, 0f)));
        Assert.True(SkillMoveTracker.TryTake(caster, 700000, out var move));
        Assert.Equal(2u, move.MovedHandle);

        // Taking one frees a slot; another caster is unaffected.
        Assert.True(SkillMoveTracker.Record(caster, 999999, new SkillMoveLedger.SkillMove(3, 9, 0f, 0f, 0f)));
        Assert.Equal(0, SkillMoveTracker.MoveCount(new PlayerState()));
    }
}
