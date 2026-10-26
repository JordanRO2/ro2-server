using System.Collections.Generic;
using RO2.Combats.Application;
using Xunit;

namespace RO2.Tests.Combat;

/// <summary>Characterization of <see cref="CastGate"/> (the 1508 precondition moved into Combat.Application in B4 S18).</summary>
public class CastGateTests
{
    /// <summary>A fake catalog: item ids, ids with a level row, and groups with a talent limit are listed explicitly.</summary>
    private sealed class FakeCatalog : ISkillCatalog
    {
        public HashSet<uint> ItemSkills = new();
        public HashSet<uint> LevelRows = new();
        public HashSet<uint> TalentGroups = new();
        public bool IsItemSkill(uint skillId) => ItemSkills.Contains(skillId);
        public bool HasLevelRow(uint skillId) => LevelRows.Contains(skillId);
        public bool HasTalentLimit(uint skillGroup) => TalentGroups.Contains(skillGroup);
    }

    private const uint ClassSkill = 312401;   // group 3124
    private const uint ItemSkill = 2_010_001;

    [Fact]
    public void ItemSkill_RefusedFirst_EvenWhenAlsoDead()
    {
        var cat = new FakeCatalog { ItemSkills = { ItemSkill } };
        // Item check precedes the dead check: an item id with a dead caster is still ItemSkill.
        Assert.Equal(CastGate.Decision.ItemSkill, CastGate.Check(cat, ItemSkill, casterIsDead: true, learnedLevelOfGroup: 0));
    }

    [Fact]
    public void DeadCaster_Refused()
    {
        var cat = new FakeCatalog();
        Assert.Equal(CastGate.Decision.Dead, CastGate.Check(cat, ClassSkill, casterIsDead: true, learnedLevelOfGroup: 5));
    }

    [Fact]
    public void TalentSkill_NotLearned_OrNoRow_Refused()
    {
        var cat = new FakeCatalog { TalentGroups = { 3124 }, LevelRows = { ClassSkill } };
        // Has a talent limit, has a row, but learned level 0 -> not learned.
        Assert.Equal(CastGate.Decision.NotLearnedTalent, CastGate.Check(cat, ClassSkill, false, learnedLevelOfGroup: 0));

        // Has a talent limit, learned, but NO row for the exact id -> still refused.
        var noRow = new FakeCatalog { TalentGroups = { 3124 } };
        Assert.Equal(CastGate.Decision.NotLearnedTalent, CastGate.Check(noRow, ClassSkill, false, learnedLevelOfGroup: 5));
    }

    [Fact]
    public void TalentSkill_LearnedWithRow_Allowed()
    {
        var cat = new FakeCatalog { TalentGroups = { 3124 }, LevelRows = { ClassSkill } };
        Assert.Equal(CastGate.Decision.Allowed, CastGate.Check(cat, ClassSkill, false, learnedLevelOfGroup: 5));
    }

    [Fact]
    public void NonTalentSkill_ExemptFromLearnedCheck_Allowed()
    {
        // No talent limit for the group: a raw 1508 of a non-talent id is allowed with no row and no learned level.
        var cat = new FakeCatalog();
        Assert.Equal(CastGate.Decision.Allowed, CastGate.Check(cat, ClassSkill, false, learnedLevelOfGroup: 0));
    }

    [Fact]
    public void ItemSkillIdRange_ExemptFromTalentCheck_EvenIfListedAsTalent()
    {
        // An id at/above FirstNonClassSkillId skips the learned check even if the group were (wrongly) talent-limited;
        // only the IsItemSkill list makes it an ItemSkill. Here it is not an item id, so it is Allowed.
        var cat = new FakeCatalog { TalentGroups = { ItemSkill / 100u } };
        Assert.Equal(CastGate.Decision.Allowed, CastGate.Check(cat, ItemSkill, false, learnedLevelOfGroup: 0));
    }
}
