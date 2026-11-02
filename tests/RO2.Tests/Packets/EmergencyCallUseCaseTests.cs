using RO2.GameData.Tables;
using RO2.Guilds.Application;
using RO2.Guilds.Domain;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>The Guild.Application Emergency Call decision: the verdict and the recipient selection, with no host involved.</summary>
public class EmergencyCallUseCaseTests
{
    private sealed class FakeDirectory(Guild? guild) : IGuildDirectory
    {
        public IReadOnlyList<uint> GetGuildMemberCharIds(uint guildId) => [];
        public Guild? GetByCharId(uint charId) => guild;
    }

    private sealed class FakeLocator(Dictionary<uint, uint> sessions) : IGuildMemberLocator
    {
        public bool IsOnline(uint charId) => sessions.ContainsKey(charId);
        public uint? SessionIdOf(uint charId) => sessions.TryGetValue(charId, out var s) ? s : null;
        public IReadOnlyList<uint> OnlineMemberCharIds(Guild guild) => [];
    }

    private static readonly IReadOnlySet<uint> Maps = new HashSet<uint> { 3101, 3201 };
    private static readonly GuildSkillLevel[] Levels = [new() { Guild_Skill_ID = 210503, Link_Skill_ID = 4000103 }];

    private static Guild MakeGuild()
    {
        var g = new Guild { Id = 7, MasterCharId = 100, Skills = new List<uint> { 210503 } };
        foreach (var id in new uint[] { 100, 101, 102, 103 })
            g.Members.Add(new GuildMember { CharId = id });
        return g;
    }

    [Fact]
    public void Plan_PromptsOnlyTheOnlineMembersOtherThanTheCaster()
    {
        var uc = new EmergencyCallUseCase(new FakeDirectory(MakeGuild()),
            new FakeLocator(new() { [100] = 1, [101] = 11, [103] = 13 }));   // 102 offline

        var plan = uc.Plan(100, 4000103, 3201, Maps, Levels);

        Assert.Equal(EmergencyCallRefusal.None, plan.Refusal);
        Assert.Equal(7u, plan.Guild!.Id);
        Assert.Equal(new[] { new EmergencyCallRecipient(101, 11), new EmergencyCallRecipient(103, 13) }, plan.Recipients);
    }

    [Fact]
    public void Plan_RefusesANonMasterAndSelectsNoRecipients()
    {
        var uc = new EmergencyCallUseCase(new FakeDirectory(MakeGuild()),
            new FakeLocator(new() { [100] = 1, [101] = 11 }));

        var plan = uc.Plan(101, 4000103, 3201, Maps, Levels);

        Assert.Equal(EmergencyCallRefusal.NotMaster, plan.Refusal);
        Assert.Empty(plan.Recipients);
    }

    [Fact]
    public void Plan_RefusesAGuildlessCasterAndAWrongMap()
    {
        var locator = new FakeLocator(new() { [101] = 11 });

        Assert.Equal(EmergencyCallRefusal.NoGuild,
            new EmergencyCallUseCase(new FakeDirectory(null), locator).Plan(100, 4000103, 3201, Maps, Levels).Refusal);
        Assert.Equal(EmergencyCallRefusal.WrongMap,
            new EmergencyCallUseCase(new FakeDirectory(MakeGuild()), locator).Plan(100, 4000103, 1100, Maps, Levels).Refusal);
    }
}
