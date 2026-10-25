using System.Text.Json;
using RO2.GameData.Tables;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Gate for the guild Emergency Call: the 2960 prompt, the master's self-shaped 2718, the 1653 answer, who
/// may cast it, and the pending call its answer consumes.
/// </summary>
/// <remarks>
/// Expected values are reference frames, both framed by enc (UNENC header 3, otherwise 5).
/// </remarks>
public class GuildEmergencyCallTests
{
    private const uint EmergencyCallLevel3 = 4000103;

    [Fact]
    public void Prompt2960_IsRetailsOnlyBody()
    {
        
        Assert.Equal(Convert.FromHexString("040000000a000000"), EconomyMiscPackets.BuildGuildEmergencyCallAsk());
        Assert.Equal(10u, EconomyMiscPackets.GuildEmergencyCallAnswerSeconds);
    }

    [Theory]
    
    [InlineData(0x0B7FC811u, "012e2e004be40000000067093d0011c87f0b0111c87f0b00000000000000000000000000000000000000000001000000")]
    
    [InlineData(0x0B83D411u, "012e2e004be40000000067093d0011d4830b0111d4830b00000000000000000000000000000000000000000001000000")]
    
    [InlineData(0x0889BF11u, "012e2e004be40000000067093d0011bf89080111bf890800000000000000000000000000000000000000000001000000")]
    public void MasterLanding2718_ReproducesRetailFramesByteForByte(uint caster, string retailHex)
    {
        
        
        var built = SkillDamagePackets.BuildSkillDamage2718(caster, EmergencyCallLevel3, caster, hits: null,
            x: 0f, y: 0f, z: 0f, castType: 0, skillEffectIndex: SkillDamagePackets.SkillEffectIndexPcModal);

        Assert.Equal(Convert.FromHexString(retailHex), built);
        Assert.Equal(48, built.Length);
    }

    [Theory]
    
    [InlineData("1171850b140500008d2fb143420d11438b89474101000000", 0x0B857111u, 1300u)]
    
    [InlineData("1157860b1d0c0000c8f98c44fa189644b12e1f4301000000", 0x0B865711u, 3101u)]
    
    [InlineData("1130820b1d0c0000879689438eb39444b0fb2e4301000000", 0x0B823011u, 3101u)]
    public void Answer1653_DescribesTheAnsweringMember(string retailHex, uint member, uint memberMap)
    {
        var (selfHandle, mapId, _, _, _, marker) = GuildEmergencyCallAnswerHandler.ParseAnswer(Convert.FromHexString(retailHex));

        Assert.Equal(member, selfHandle);
        Assert.Equal(0x11u, selfHandle & 0xFF);   
        Assert.Equal(memberMap, mapId);
        Assert.Equal(1u, marker);                 
    }

    [Fact]
    public void EmergencyCallRows_ComeFromTheShippedSkillTable()
    {
        var groups = GuildEmergencyCallService.ParseEmergencyCallGroups(File.ReadAllBytes(DataFile("Skill_Table_Base.json")));

        var only = Assert.Single(groups);
        Assert.Equal(40001u, only.Key);
        Assert.Equal(new uint[] { 3101, 3201 }, only.Value.Order().ToArray());
    }

    [Theory]
    [InlineData("3101,3201,", new uint[] { 3101, 3201 })]
    [InlineData(" 3101 , 3201 ", new uint[] { 3101, 3201 })]
    [InlineData("", new uint[0])]
    [InlineData(null, new uint[0])]
    public void TargetingMapCell_Parses(string? cell, uint[] expected)
        => Assert.Equal(expected, GuildEmergencyCallService.ParseMapList(cell).Order().ToArray());

    [Fact]
    public void Validate_RequiresTheMasterTheLearnedLevelAndATargetMap()
    {
        var levels = ShippedGuildSkillLevels();
        var maps = new HashSet<uint> { 3101, 3201 };
        var guild = new Guild { Id = 7, MasterCharId = 100, Skills = new List<uint> { 210503 } };
        guild.Members.Add(new GuildMember { CharId = 100 });
        guild.Members.Add(new GuildMember { CharId = 101 });

        Assert.Equal(GuildEmergencyCallService.CastRefusal.None,
            GuildEmergencyCallService.Validate(guild, 100, EmergencyCallLevel3, 3201, maps, levels));
        Assert.Equal(GuildEmergencyCallService.CastRefusal.NotMaster,
            GuildEmergencyCallService.Validate(guild, 101, EmergencyCallLevel3, 3201, maps, levels));
        Assert.Equal(GuildEmergencyCallService.CastRefusal.NoGuild,
            GuildEmergencyCallService.Validate(null, 100, EmergencyCallLevel3, 3201, maps, levels));
        
        Assert.Equal(GuildEmergencyCallService.CastRefusal.NotLearned,
            GuildEmergencyCallService.Validate(guild, 100, 4000101, 3201, maps, levels));
        Assert.Equal(GuildEmergencyCallService.CastRefusal.WrongMap,
            GuildEmergencyCallService.Validate(guild, 100, EmergencyCallLevel3, 1100, maps, levels));
    }

    [Fact]
    public void PendingCall_IsAnsweredOncePerPromptedMemberInsideTheWindow()
    {
        const uint guildId = 0xEC0001;
        var issued = new DateTime(2026, 9, 24, 2, 10, 20, 673, DateTimeKind.Utc);
        GuildEmergencyCallService.Record(guildId, 100, 3101, 314.9f, 1199.3f, 175.1f, issued, new uint[] { 200, 201 });

        
        
        Assert.False(GuildEmergencyCallService.TryConsume(guildId, 202, issued.AddSeconds(1), out _));

        
        Assert.True(GuildEmergencyCallService.TryConsume(guildId, 200, issued.AddSeconds(9.84), out var call));
        Assert.Equal(3101u, call.MapId);
        Assert.Equal((314.9f, 1199.3f, 175.1f), (call.X, call.Y, call.Z));
        Assert.Equal(issued.AddSeconds(10), call.ExpiresUtc);

        
        Assert.False(GuildEmergencyCallService.TryConsume(guildId, 200, issued.AddSeconds(9.9), out _));

        
        Assert.False(GuildEmergencyCallService.TryConsume(guildId, 201, issued.AddSeconds(10.01), out _));
        Assert.False(GuildEmergencyCallService.TryConsume(guildId, 201, issued.AddSeconds(1), out _));
    }

    [Fact]
    public void NewCall_ReplacesTheGuildsOldOne()
    {
        const uint guildId = 0xEC0002;
        var first = new DateTime(2026, 9, 24, 7, 35, 56, DateTimeKind.Utc);
        GuildEmergencyCallService.Record(guildId, 100, 3201, 1f, 2f, 3f, first, new uint[] { 200 });
        GuildEmergencyCallService.Record(guildId, 100, 3101, 4f, 5f, 6f, first.AddSeconds(2), new uint[] { 201 });

        Assert.False(GuildEmergencyCallService.TryConsume(guildId, 200, first.AddSeconds(3), out _));
        Assert.True(GuildEmergencyCallService.TryConsume(guildId, 201, first.AddSeconds(3), out var call));
        Assert.Equal(3101u, call.MapId);
    }

 /// <summary>The shipped Guild_Skill_Level rows, reduced to the two columns the validation reads.</summary>
    private static List<GuildSkillLevel> ShippedGuildSkillLevels()
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(DataFile("Guild_Skill_Level.json")));
        var rows = new List<GuildSkillLevel>();
        foreach (var row in doc.RootElement.EnumerateArray())
            rows.Add(new GuildSkillLevel
            {
                Guild_Skill_ID = row.GetProperty("Guild_Skill_ID").GetUInt32(),
                Link_Skill_ID = row.GetProperty("Link_Skill_ID").GetUInt32(),
            });
        Assert.Contains(rows, r => r.Guild_Skill_ID == 210503 && r.Link_Skill_ID == EmergencyCallLevel3);
        return rows;
    }

 /// <summary>Walks up from the test output path to the repository's data directory.</summary>
    internal static string DataFile(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "data", name);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("no ancestor of " + AppContext.BaseDirectory + " holds data/" + name);
    }
}

/// <summary>
/// Gate for 2817 NfyCoolTimeClear: its body, the refund ahead of a refusal, and the groups a reset-all skill
/// clears, checked against reference frames.
/// </summary>
public class CoolTimeClear2817Tests
{
    [Theory]
    [InlineData(4301u, "cd100000")]   
    [InlineData(4121u, "19100000")]   
    [InlineData(4101u, "05100000")]   
    public void Body_IsTheSkillGroup(uint group, string retailHex)
        => Assert.Equal(Convert.FromHexString(retailHex), SkillCoolTimeReset.BuildCoolTimeClear2817(group));

    [Fact]
    public void ResetAllSkills_ComeFromTheShippedDescriptions()
    {
        var groups = SkillCoolTimeReset.ParseResetAllGroups(
            File.ReadAllBytes(GuildEmergencyCallTests.DataFile("Skill_Table_SkillLevel.json")),
            File.ReadAllBytes(GuildEmergencyCallTests.DataFile(Path.Combine("strings", "String_Skill_Description.json"))));

        Assert.Equal(new uint[] { 3230, 4312, 5304 }, groups.Order().ToArray());
    }

    [Fact]
    public void ResettableGroups_AreLearnedSkillsMinusJobMasterOnes()
    {
        var groups = ShippedResettable();

        foreach (var cleared in new uint[] { 4301, 4121, 4345, 4313, 4302, 6203, 1202 })
            Assert.Contains(cleared, groups);       
        foreach (var kept in new uint[] { 4300, 20081, 40005, 90004, 91002 })
            Assert.DoesNotContain(kept, groups);    
    }

    [Theory]
    
    
    
    [InlineData(new uint[] { 4301, 4121, 90004, 4300, 4312 }, new[] { 7.39, 0.53, 270.15, 165.36, 37.65 }, new uint[] { 4121, 4301 })]
    
    [InlineData(new uint[] { 4301, 4312 }, new[] { 21.85, 37.64 }, new uint[] { 4301 })]
    public void DirtyPlan_ClearsWhatRetailCleared(uint[] groups, double[] secondsLeft, uint[] expected)
    {
        var now = new DateTime(2026, 9, 23, 7, 22, 57, 694, DateTimeKind.Utc);
        var cooldowns = new Dictionary<uint, DateTime>();
        for (var i = 0; i < groups.Length; i++)
            cooldowns[groups[i]] = now.AddSeconds(secondsLeft[i]);
        var resettable = ShippedResettable();

        var cleared = SkillCoolTimeReset.TakeResettableGroups(cooldowns, 4312, now, resettable.Contains);

        Assert.Equal(expected, cleared.ToArray());
        foreach (var group in expected)
            Assert.False(cooldowns.ContainsKey(group));     
        Assert.True(cooldowns.ContainsKey(4312));           
    }

    [Fact]
    public void Refund_EndsOnlyACooldownThatIsStillRunning()
    {
        var now = new DateTime(2026, 9, 23, 6, 43, 50, 647, DateTimeKind.Utc);
        var cooldowns = new Dictionary<uint, DateTime>
        {
            [4101] = now.AddSeconds(0.279),   
            [4102] = now.AddSeconds(-1),
        };

        Assert.True(SkillCoolTimeReset.TryEndCooldown(cooldowns, 4101, now));
        Assert.False(cooldowns.ContainsKey(4101));
        Assert.False(SkillCoolTimeReset.TryEndCooldown(cooldowns, 4102, now));   
        Assert.False(SkillCoolTimeReset.TryEndCooldown(cooldowns, 4121, now));   
    }

    private static IReadOnlySet<uint> ShippedResettable()
        => SkillCoolTimeReset.ParseResettableGroups(
            File.ReadAllBytes(GuildEmergencyCallTests.DataFile("Skill_Table_Base.json")),
            File.ReadAllBytes(GuildEmergencyCallTests.DataFile("Talent_Table.json")));
}

/// <summary>Gate for the monster's target choice: a target is kept until another row passes it by more than 10%.</summary>
public class ThreatTargetHysteresisTests
{
    private static Dictionary<uint, NpcStateManager.ThreatRow> Table(params (uint Handle, long Threat)[] rows)
        => rows.ToDictionary(r => r.Handle, r => new NpcStateManager.ThreatRow { AttackerHandle = r.Handle, Threat = r.Threat });

    [Fact]
    public void Target_IsKeptUpTo110Percent()
        => Assert.Equal(0x0111u, NpcStateManager.SelectAggroTarget(Table((0x0111, 100), (0x0211, 110)), 0x0111));

    [Fact]
    public void Target_MovesAbove110Percent()
        => Assert.Equal(0x0211u, NpcStateManager.SelectAggroTarget(Table((0x0111, 100), (0x0211, 111)), 0x0111));

    [Fact]
    public void NoTarget_TakesTheTopRow()
        => Assert.Equal(0x0211u, NpcStateManager.SelectAggroTarget(Table((0x0111, 100), (0x0211, 101)), 0));

    [Fact]
    public void TargetOffTheTable_TakesTheTopRow()
        => Assert.Equal(0x0211u, NpcStateManager.SelectAggroTarget(Table((0x0111, 100), (0x0211, 101)), 0x0311));

    [Fact]
    public void EmptyTable_HasNoTarget()
        => Assert.Equal(0u, NpcStateManager.SelectAggroTarget(Table(), 0x0111));
}
