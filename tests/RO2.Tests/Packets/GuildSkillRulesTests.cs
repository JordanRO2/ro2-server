using RO2.GameData.Tables;
using RO2.Guilds.Domain;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>Pure guild skill rules, on a synthetic table.</summary>
public class GuildSkillRulesTests
{
    private static readonly IReadOnlyDictionary<uint, GuildSkillLevel> Table = new[]
    {
        new GuildSkillLevel { Guild_Skill_ID = 10101, Need_GuildLevel = 1, Skill_LearnType = 0 },
        new GuildSkillLevel { Guild_Skill_ID = 10102, Need_GuildLevel = 2, Skill_LearnType = 0, Need_Skill_ID_1 = 10101, Link_Skill_ID = 5502 },
        new GuildSkillLevel { Guild_Skill_ID = 10201, Need_GuildLevel = 1, Skill_LearnType = 0, Need_Skill_ID_1 = 10101 },
        new GuildSkillLevel { Guild_Skill_ID = 20101, Need_GuildLevel = 3, Skill_LearnType = 1 },
        new GuildSkillLevel { Guild_Skill_ID = 20102, Need_GuildLevel = 3, Skill_LearnType = 1, Need_Skill_ID_1 = 20101 },
    }.ToDictionary(r => r.Guild_Skill_ID);

    [Fact]
    public void IdLayoutAndLineLevels()
    {
        Assert.Equal(101u, GuildSkillRules.LineOf(10102));
        Assert.Equal(2, GuildSkillRules.LevelOf(10102));
        var levels = GuildSkillRules.LineLevels([10101, 10102, 20101]);
        Assert.Equal(2, levels[101]);
        Assert.Equal(1, levels[201]);
    }

    [Fact]
    public void UnspentPoints_CountsOnlyPointLinesAndClamps()
    {
        Assert.Equal(4, GuildSkillRules.UnspentPoints(5, [], Table));
        Assert.Equal(2, GuildSkillRules.UnspentPoints(5, [10102, 20101], Table));
        Assert.Equal(0, GuildSkillRules.UnspentPoints(1, [10101], Table));
        Assert.Equal(0, GuildSkillRules.UnspentPoints(0, [], Table));
    }

    [Fact]
    public void PlanLearn_OrdersByPrerequisiteAndReportsRefusals()
    {
        var plan = GuildSkillRules.PlanLearn([10102, 10101], [], 5, Table, out var r);
        Assert.Equal(new uint[] { 10101, 10102 }, plan);
        Assert.Equal(GuildSkillLearnRefusal.None, r);

        Assert.Null(GuildSkillRules.PlanLearn([], [], 5, Table, out r));
        Assert.Equal(GuildSkillLearnRefusal.WrongSkillIndex, r);
        Assert.Null(GuildSkillRules.PlanLearn([10101, 10102], [], 2, Table, out r));
        Assert.Equal(GuildSkillLearnRefusal.NotEnoughPoints, r);
        Assert.Null(GuildSkillRules.PlanLearn([20101], [], 5, Table, out r));
        Assert.Equal(GuildSkillLearnRefusal.WrongSkillIndex, r);
        Assert.Null(GuildSkillRules.PlanLearn([10201], [], 5, Table, out r));
        Assert.Equal(GuildSkillLearnRefusal.MissingNeedSkill1, r);
    }

    [Fact]
    public void PlanBook_Verdicts()
    {
        Assert.Equal(GuildSkillBookVerdict.Learn, GuildSkillRules.PlanBook(20101, [], 3, Table));
        Assert.Equal(GuildSkillBookVerdict.NotLearnable, GuildSkillRules.PlanBook(20101, [], 2, Table));
        Assert.Equal(GuildSkillBookVerdict.NotLearnable, GuildSkillRules.PlanBook(10101, [], 5, Table));
        Assert.Equal(GuildSkillBookVerdict.NotLearnable, GuildSkillRules.PlanBook(0, [], 5, Table));
        Assert.Equal(GuildSkillBookVerdict.AlreadyLearned, GuildSkillRules.PlanBook(20101, [20102], 3, Table));
        Assert.Equal(GuildSkillBookVerdict.PreviousLevelMissing, GuildSkillRules.PlanBook(20102, [], 3, Table));
    }

    [Fact]
    public void SkillListWithGuildLinks_KeepsHigherLevelPerGroup()
    {
        var list = GuildSkillRules.SkillListWithGuildLinks(
            [new KeyValuePair<uint, byte>(55, 1), new KeyValuePair<uint, byte>(7, 3)], [10101, 10102], Table);
        Assert.Contains((55u, (byte)2), list);
        Assert.Contains((7u, (byte)3), list);
        Assert.Equal(2, list.Count);
    }

    [Fact]
    public void PlanGuildLinkBurst_OneCumulativeListPerMissingLink()
    {
        var own = new Dictionary<uint, byte> { [7] = 3 };
        Assert.Empty(GuildSkillRules.PlanGuildLinkBurst(own, null, [10102], Table));

        var burst = GuildSkillRules.PlanGuildLinkBurst(own, new Dictionary<uint, byte>(own), [10101, 10102], Table);
        var only = Assert.Single(burst);
        Assert.Equal([(7u, (byte)3), (55u, (byte)2)], only);

        Assert.Empty(GuildSkillRules.PlanGuildLinkBurst(own, new Dictionary<uint, byte> { [7] = 3, [55] = 2 }, [10102], Table));
        Assert.Empty(GuildSkillRules.PlanGuildLinkBurst(own, new Dictionary<uint, byte>(own), [10101], Table));
    }
}
