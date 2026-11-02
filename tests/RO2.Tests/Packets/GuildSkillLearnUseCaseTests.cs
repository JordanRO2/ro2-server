using RO2.GameData.Tables;
using RO2.Guilds.Application;
using RO2.Guilds.Domain;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>The Guild.Application skill learn: plan + apply under the gate + one persist, no host involved.</summary>
public class GuildSkillLearnUseCaseTests
{
    private sealed class CountingLedger : IGuildSkillLedger
    {
        public readonly List<uint> Writes = [];
        public Task AddSkillAsync(Guild guild, uint skillId) { Writes.Add(skillId); return Task.CompletedTask; }
    }

    private static readonly Dictionary<uint, GuildSkillLevel> Table = new()
    {
        [101] = new() { Guild_Skill_ID = 101, Skill_LearnType = GuildSkillRules.PointLearnType },
        [102] = new() { Guild_Skill_ID = 102, Skill_LearnType = GuildSkillRules.PointLearnType },
        [201] = new() { Guild_Skill_ID = 201, Skill_LearnType = GuildSkillRules.BookLearnType },
        [202] = new() { Guild_Skill_ID = 202, Skill_LearnType = GuildSkillRules.BookLearnType, Need_Skill_ID_1 = 201 },
    };

    [Fact]
    public async Task Learn_AppliesTheLevelsAndWritesOnce()
    {
        var ledger = new CountingLedger();
        var g = new Guild();

        var r = await new GuildSkillLearnUseCase(ledger).LearnAsync(g, [101, 102], 10, Table);

        Assert.Equal(new uint[] { 101, 102 }, r.Learned);
        Assert.Equal(new uint[] { 101, 102 }, g.Skills);
        Assert.Equal((byte)7, r.UnspentAfter);     // 10 - 1 - 2 (line 1 held at level 2)
        Assert.Equal(new uint[] { 102 }, ledger.Writes);
    }

    [Fact]
    public async Task Learn_RefusedChangesNothingAndWritesNothing()
    {
        var ledger = new CountingLedger();
        var g = new Guild();

        var r = await new GuildSkillLearnUseCase(ledger).LearnAsync(g, [101, 102], 2, Table);   // 1 point, 2 requested

        Assert.Null(r.Learned);
        Assert.Equal(GuildSkillLearnRefusal.NotEnoughPoints, r.Refusal);
        Assert.Empty(g.Skills);
        Assert.Empty(ledger.Writes);
    }

    [Fact]
    public async Task LearnBook_LearnsWhenAllowedAndKeepsTheBookOtherwise()
    {
        var ledger = new CountingLedger();
        var useCase = new GuildSkillLearnUseCase(ledger);
        var g = new Guild();

        var missing = await useCase.LearnBookAsync(g, 202, 10, Table);
        Assert.Equal(GuildSkillBookVerdict.PreviousLevelMissing, missing.Verdict);
        Assert.Empty(g.Skills);
        Assert.Empty(ledger.Writes);

        var learn = await useCase.LearnBookAsync(g, 201, 10, Table);
        Assert.Equal(GuildSkillBookVerdict.Learn, learn.Verdict);
        Assert.Equal(new uint[] { 201 }, g.Skills);
        Assert.Equal(new uint[] { 201 }, ledger.Writes);

        var again = await useCase.LearnBookAsync(g, 201, 10, Table);
        Assert.Equal(GuildSkillBookVerdict.AlreadyLearned, again.Verdict);
        Assert.Single(ledger.Writes);
    }
}
