using System.Text.Json;
using RO2.GameData.Tables;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Party battle-ready check: the 1500/1501 requests and the 2697, 2698, 2699 and 2700 answers. The non-zero
/// result codes come from the client's readers.
/// </summary>
public class PartyReadyCheckPacketsTests
{
    [Fact]
    public void Answer2698_IsRetailsZeroWord()
    {
        
        
        Assert.Equal("0000", Hex(PartyReadyCheckPackets.BuildAnsPartyFightingTrim2698(PartyReadyCheckPackets.ResultOk)));
    }

    [Fact]
    public void RefusalCodes_AreABareU16()
    {
        
        
        
        Assert.Equal("6f3d", Hex(PartyReadyCheckPackets.BuildAnsPartyFightingTrim2698(PartyReadyCheckPackets.ResultAlreadyChecking)));
        Assert.Equal("6d3d", Hex(PartyReadyCheckPackets.BuildAnsPartyFightingTrimVote2700(PartyReadyCheckPackets.ResultAlreadyVoted)));
    }

    [Fact]
    public void Start2699_IsRetailsFrame()
    {
        
        
        Assert.Equal("0110100040e4000000008df1eca7b3b6e00d",
            Hex(PartyReadyCheckPackets.BuildNfyPartyFightingTrimStart2699(0x0DE0B6B3A7ECF18DUL)));
        Assert.Equal("0110100040e4000000005f691c7db3e8c937",
            Hex(PartyReadyCheckPackets.BuildNfyPartyFightingTrimStart2699(0x37C9E8B37D1C695FUL)));
    }

    [Fact]
    public void Result2697_OneReadyNoneMissing_IsRetailsFrame()
    {
        
        
        Assert.Equal("0400000001000000", Hex(PartyReadyCheckPackets.BuildNfyPartyFightingTrimResult2697(1, 0, [])));
    }

    [Fact]
    public void Result2697_IsU32PrefixedCountsAndFixedNames()
    {
        
        
        var body = PartyReadyCheckPackets.BuildNfyPartyFightingTrimResult2697(2, 1, ["Azuv"]);
        Assert.Equal(4 + 4 + 42, body.Length);
        var expected = "2e000000" + "02" + "01" + "0100" + "41007a0075007600" + new string('0', 2 * (42 - 8));
        Assert.Equal(expected, Hex(body));
    }

    [Fact]
    public void Result2697_KeepsANulInEachName()
    {
        
        var body = PartyReadyCheckPackets.BuildNfyPartyFightingTrimResult2697(0, 2, [new string('x', 25), ""]);
        Assert.Equal(8 + 2 * 42, body.Length);
        Assert.Equal(new string('x', 20), System.Text.Encoding.Unicode.GetString(body, 8, 40));
        Assert.Equal(0, body[8 + 40] | body[8 + 41]);
        Assert.All(body.AsSpan(8 + 42, 42).ToArray(), b => Assert.Equal(0, b));
    }

    [Fact]
    public void Start2699_SkipsTheInitiatorAndOfflineMembers()
    {
        
        uint[] roster = [7, 3, 9, 4];
        var online = new HashSet<uint> { 7, 3, 4 };
        Assert.Equal(new uint[] { 3, 4 }, PartyReadyCheckService.MembersToNotify(roster, online.Contains, initiatorSessionId: 7));
        Assert.Empty(PartyReadyCheckService.MembersToNotify([7], online.Contains, initiatorSessionId: 7));
    }

    [Fact]
    public void Tally_CountsEveryoneButTheInitiator()
    {
        PartyReadyCheckService.RosterEntry[] roster =
        [
            new(1, "Lead", true), new(2, "Yes", true), new(3, "No", true), new(4, "Silent", true), new(5, "Offline", false),
        ];
        var votes = new Dictionary<ulong, byte> { [2] = PartyReadyCheckPackets.VoteReady, [3] = 0 };
        var (ready, notReady, names) = PartyReadyCheckService.Tally(roster, initiatorGid: 1, votes);
        
        Assert.Equal(1, ready);
        Assert.Equal(3, notReady);
        Assert.Equal(new[] { "No", "Silent", "Offline" }, names);
    }

    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();
}

/// <summary>
/// 1652 ReqGuildSkillLearnBook and the guild skill book rules, checked against the shipped data.
/// </summary>
public class GuildSkillBookRetailFrameTests
{
 /// <summary>The guild's 2723 list before the book is learned: 18 lines, 2203 at level 4.</summary>
    private static readonly uint[] GuildBefore =
    [
        120105, 120405, 120505, 120605, 130201, 200105, 210107, 210207, 210307, 210407, 210503,
        220105, 220205, 220304, 220409, 220510, 220610, 220710,
    ];

    [Fact]
    public void AnswerBurst_IsByteExact()
    {
        Assert.Equal("0000", Hex(GuildSkillBookLearn.BuildAnsGuildSkillLearnBook2959(GuildSkillBookLearn.ResultLearned)));
        Assert.Equal("07000000010100915c0300", Hex(GuildSkillLearnPackets.BuildNfyGuildSkillLearn2724(1, [220305])));
        Assert.Equal("01161600a6e211792805000019042a375c05a83d040e0001",
            Hex(CombatPackets.BuildUseItem2329(0x05287911, 25, 4, 0x0E043DA8_055C372AUL, 0, useSource: 1)));
        Assert.Equal("01141400a7e20000000011792805296b420111792805",
            Hex(LootNotifyPackets.BuildUseItemNotify2330(0x05287911, 21130025)));
    }

    [Fact]
    public void Request1652_IsTheBookUid()
    {
        Assert.True(GuildSkillBookLearn.TryParseRequest(Convert.FromHexString("2a375c05a83d040e"), out var uid));
        Assert.Equal(0x0E043DA8_055C372AUL, uid);
        Assert.False(GuildSkillBookLearn.TryParseRequest(new byte[7], out _));
    }

    [Fact]
    public void FailureCodes_AreTheConsumersSwitch()
    {
        
        Assert.Equal("3100", Hex(GuildSkillBookLearn.BuildAnsGuildSkillLearnBook2959(GuildSkillBookLearn.ResultPreviousLevelMissing)));
        Assert.Equal("4600", Hex(GuildSkillBookLearn.BuildAnsGuildSkillLearnBook2959(GuildSkillBookLearn.ResultAlreadyLearned)));
    }

    [Fact]
    public async Task Plan_FollowsTheLearnType1Rows()
    {
        var table = GuildSkillLearnPackets.Index(await GuildSkillLevelsAsync());
        Assert.Equal(GuildSkillBookLearn.Verdict.Learn, GuildSkillBookLearn.Plan(220305, GuildBefore, 20, table));
        Assert.Equal(GuildSkillBookLearn.Verdict.AlreadyLearned, GuildSkillBookLearn.Plan(220305, [.. GuildBefore, 220305], 20, table));
        Assert.Equal(GuildSkillBookLearn.Verdict.AlreadyLearned, GuildSkillBookLearn.Plan(220304, GuildBefore, 20, table));
        Assert.Equal(GuildSkillBookLearn.Verdict.PreviousLevelMissing, GuildSkillBookLearn.Plan(220305, [220303], 20, table));
        Assert.Equal(GuildSkillBookLearn.Verdict.Learn, GuildSkillBookLearn.Plan(220301, [], 1, table));
        
        Assert.Equal(GuildSkillBookLearn.Verdict.NotLearnable, GuildSkillBookLearn.Plan(120501, [], 20, table));
        Assert.Equal(GuildSkillBookLearn.Verdict.NotLearnable, GuildSkillBookLearn.Plan(210601, [], 20, table));
        Assert.Equal(GuildSkillBookLearn.Verdict.NotLearnable, GuildSkillBookLearn.Plan(0, [], 20, table));
        
        Assert.Equal(GuildSkillLearnPackets.UnspentPoints(20, GuildBefore, table),
            GuildSkillLearnPackets.UnspentPoints(20, [.. GuildBefore, 220305], table));
    }

    [Fact]
    public async Task SkillList2488_AfterTheBook_IsRetailsBody()
    {
        var table = GuildSkillLearnPackets.Index(await GuildSkillLevelsAsync());
        
        uint[] own = [410101, 410201, 410301, 410602, 410803, 412105, 412505, 430003, 430106, 430201, 430305,
            430802, 430909, 431205, 431310, 431403, 434510, 5040003];
        var ownSkills = own.Select(id => new KeyValuePair<uint, byte>(id / 100, (byte)(id % 100)));
        var list = GuildSkillBookLearn.SkillListWithGuildLinks(ownSkills, [.. GuildBefore, 220305], table);
        const string retail2488 =
            "0284008400b3e3000000001e000000f541060059420600bd420600ea430600b3440600c9490600594b0600b38f06001a900600" +
            "79900600e1900600d29206003d93060065940600ce9406002b9506004ea1060001093d0067093d00cd093d00950a3d00fb0a3d00" +
            "5f0b3d00c30b3d00270c3d008d0c3d00f20c3d00560d3d00ba0d3d0083e74c00";
        Assert.Equal(retail2488, Hex(SkillPackets.BuildUseSkillList2488(list)));
    }

    private static async Task<List<GuildSkillLevel>> GuildSkillLevelsAsync()
    {
        await using var fs = File.OpenRead(DataFile("Guild_Skill_Level.json"));
        return (await JsonSerializer.DeserializeAsync<List<GuildSkillLevel>>(fs))!;
    }

 /// <summary>Walks up from the test output path to the repository's data directory.</summary>
    private static string DataFile(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "data", name);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("no ancestor of " + AppContext.BaseDirectory + " holds data/" + name);
    }

    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();
}

/// <summary>
/// The Zone side of a close-range chat room join and a joiner's exit, and the password flag read by the sign click (2763).
/// </summary>
public class CloseRangeJoinRetailFrameTests
{
    [Fact]
    public void Join2764And2770_AreByteExact()
    {
        
        Assert.Equal("010c0c0063e411082b0211082b02", Hex(CloseRangeChatPackets.BuildNfyJoinCloseRangeChatRoom2764(0x022B0811)));
        Assert.Equal("010e0e0067e411a12a0211a12a020a02", Hex(CloseRangeChatPackets.BuildNfyUpdateJoinMemberCount2770(0x022AA111, 10, 2)));
        
        Assert.Equal("010c0c0063e411d6b70511d6b705", Hex(CloseRangeChatPackets.BuildNfyJoinCloseRangeChatRoom2764(0x05B7D611)));
        Assert.Equal("010e0e0067e41148a4051148a4050a02", Hex(CloseRangeChatPackets.BuildNfyUpdateJoinMemberCount2770(0x05A44811, 10, 2)));
    }

    [Fact]
    public void JoinerExit2765And2770_AreByteExact()
    {
        
        Assert.Equal("010c0c0064e41191cf041191cf04", Hex(CloseRangeChatPackets.BuildNfyExitCloseRangeChatRoom2765(0x04CF9111)));
        Assert.Equal("010e0e0067e41140c7041140c7040a01", Hex(CloseRangeChatPackets.BuildNfyUpdateJoinMemberCount2770(0x04C74011, 10, 1)));
    }

    [Fact]
    public void Sign2763_PasswordFlagIsBlob110()
    {
        
        
        var name = new byte[102];
        var guid = Convert.FromHexString("b8207c76564c9d4a8de17b8381f9d5af");
        var open = CloseRangeChatPackets.BuildNfyOpenCloseRangeChatRoom2763(0x0B744611u, name, 10, guid);
        var locked = CloseRangeChatPackets.BuildNfyOpenCloseRangeChatRoom2763(0x0B744611u, name, 10, guid, hasPassword: true);
        const int blob110 = 3 + 110; 
        Assert.Equal(CloseRangeChatPackets.SignNoPassword, open[blob110]);
        Assert.Equal(CloseRangeChatPackets.SignHasPassword, locked[blob110]);
        locked[blob110] = open[blob110];
        Assert.Equal(open, locked);
    }

    [Fact]
    public void Registry_FindsAnOpenRoom()
    {
        var guid = Guid.NewGuid().ToByteArray();
        Assert.Null(CloseRangeChatRooms.Find(guid));
        CloseRangeChatRooms.Register(new CloseRangeRoom(CloseRangeChatRooms.IdOf(guid), 991, 0x05A44811u, 10));
        Assert.Equal(0x05A44811u, CloseRangeChatRooms.Find(guid)?.OwnerHandle);
        Assert.Null(CloseRangeChatRooms.Find(guid.AsSpan(0, 15)));
        Assert.True(CloseRangeChatRooms.ResolveExit(guid, 991) is CloseRangeOwnerExited);
        Assert.Null(CloseRangeChatRooms.Find(guid));
    }

    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();
}
