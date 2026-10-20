using System.Buffers.Binary;
using RO2.Domain.Entities;
using RO2.Network.Packets;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Guild roster messages that identify a member by GID: 2571 NfyGuildMemberRemove, 2573 NfyGuildMemberAdd and the
/// own-record lookup of 2721 AnsGuildSetBribePercent, plus the title editor's answers 2633 AnsGuildModifyAuthority
/// and 2637 NfyGuildModifyAuthority.
/// </summary>
public class UnitA0GuildRosterAuthorityTests
{
    private static string Hex(ReadOnlySpan<byte> b) => Convert.ToHexString(b).ToLowerInvariant();

 /// <summary>Byte length of the UTF-16 name at <paramref name="off"/>, its NUL included.</summary>
    private static int NameSpan(ReadOnlySpan<byte> b, int off)
    {
        var i = off;
        while (b[i] != 0 || b[i + 1] != 0) i += 2;
        return i + 2 - off;
    }

 /// <summary>The char-list UID that the 1417 answer assigns to <paramref name="charId"/>. The client logs in with it
 /// and keeps it as its own GID, which the 2721 consumer uses for its lookup.</summary>
    private static ulong CharListUid(uint charId)
    {
        using var w = new PacketWriter(256);
        CharacterSerializer.WriteCharacterList(w, 7, new[] { new Character { Id = charId, Name = "Tester" } });
        return BinaryPrimitives.ReadUInt64LittleEndian(w.ToArray().AsSpan(10));
    }

    
    [Theory]
    
    [InlineData("032dfbcba076b8434a815d7f4cf2b6c68a1c42e8a7b3b6e00d4e0061006d0061006b00750072006f0000000200000000f0499aeff67f0000000000000000000079ee97a56c1c42e8a7b3b6e00d",
        "Namakuro", (byte)3)]
    
    
    [InlineData("0287d03180d6fd9a4195a0b6d0c7a981558291b7a7b3b6e00d6f0030004d00610076006900730030006f000000000000f0499aeff67f000001000000000000000200000000338ff3a7b3b6e00d",
        "o0Mavis0o", (byte)2)]
    public void Nfy2571_MatchesRetailOnEveryFieldTheClientReads(string retailHex, string name, byte reason)
    {
        
        
        
        var retail = Convert.FromHexString(retailHex);
        var g = new Guild { Id = 1, Guid16 = retail.AsSpan(1, 16).ToArray(), Name = "G" };
        var removed = new GuildMember { Gid = BinaryPrimitives.ReadUInt64LittleEndian(retail.AsSpan(17)), Name = name };
        var actor = BinaryPrimitives.ReadUInt64LittleEndian(retail.AsSpan(69));

        var ours = GuildPackets.BuildNfyGuildMemberRemove(g, removed, reason, actor);

        Assert.Equal(77, ours.Length);
        var named = 25 + NameSpan(retail, 25);
        Assert.Equal(Hex(retail.AsSpan(0, named)), Hex(ours.AsSpan(0, named)));
        Assert.Equal(Hex(retail.AsSpan(69)), Hex(ours.AsSpan(69)));
        Assert.All(ours.AsSpan(named, 69 - named).ToArray(), b => Assert.Equal(0, b));
    }

    [Fact]
    public async Task MemberGid_IsTheCharactersOwnGid_InEveryRosterMessage()
    {
        
        
        
        
        
        const uint charId = 0x7E57_2573u;
        var g = new Guild { Id = 0x7E57_2573u, Guid16 = new byte[16], Name = "G" };
        var m = await GuildService.Instance.AddMemberAsync(g, charId, "Joiner", 50, 13);
        var uid = CharListUid(charId);

        Assert.Equal(uid, m.Gid);
        var view = new GuildRosterPackets.MemberView(m, 50, 13, 3, 50, 0, 0, 1100);
        Assert.Equal(uid, BinaryPrimitives.ReadUInt64LittleEndian(GuildRosterPackets.BuildMemberRecord(view)));
        Assert.Equal(uid, BinaryPrimitives.ReadUInt64LittleEndian(GuildRosterPackets.BuildNfyGuildMemberAdd(g, view).AsSpan(17)));
        var remove = GuildPackets.BuildNfyGuildMemberRemove(g, m, 3, m.Gid);
        Assert.Equal(uid, BinaryPrimitives.ReadUInt64LittleEndian(remove.AsSpan(17)));
        Assert.Equal(uid, BinaryPrimitives.ReadUInt64LittleEndian(remove.AsSpan(69)));
    }

    
    [Fact]
    public void Nfy2573_JoinersOwnCopy_MatchesRetail()
    {
        
        
        
        
        const string retail =
            "01d003e843ff6eee4d93467b13c0bd4fdd"
            + "34209ba7b3b6e00d0000000000000000000000000032000d00030032000000000000000000004822ab0c8790e640000000008790e640"
            + "0928044c040000000000000000000000000000"
            + "590069006c0061007100750069006c006500730000000000000000000000000000000000000000000000";
        var expected = Convert.FromHexString(retail);
        Assert.Equal(132, expected.Length);

        var g = new Guild { Id = 1, Guid16 = expected.AsSpan(1, 16).ToArray(), Name = "Heaven's Gate" };
        double retailJoin = BinaryPrimitives.ReadDoubleLittleEndian(expected.AsSpan(17 + 38));
        var member = new GuildMember
        {
            Gid = 0x0DE0B6B3A79B2034UL,
            Name = "Yilaquiles",
            AuthId = 9,
            JoinedUtc = DateTime.SpecifyKind(DateTime.FromOADate(retailJoin), DateTimeKind.Local).ToUniversalTime(),
        };
        var view = new GuildRosterPackets.MemberView(member, Level: 50, Class: 13, Profession: 3, JobLevel: 50,
            ProJobLevel: 40, ProJobId: 4, MapId: 1100);
        var ours = GuildRosterPackets.BuildNfyGuildMemberAdd(g, view);

        Assert.Equal(132, ours.Length);
        Assert.Equal(Hex(expected.AsSpan(0, 17 + 38)), Hex(ours.AsSpan(0, 17 + 38)));
        Assert.Equal(Hex(expected.AsSpan(17 + 46)), Hex(ours.AsSpan(17 + 46)));
        Assert.InRange(Math.Abs(BinaryPrimitives.ReadDoubleLittleEndian(ours.AsSpan(17 + 38)) - retailJoin), 0, 1.0 / 86_400_000);
    }

    
    private const uint Master = 0x7E57_2633u;

 /// <summary>Guild roster fixture: index 0 "Gatekeeper", index 1 "Gatewarden".</summary>
    private static Guild GatekeeperGuild()
    {
        var g = new Guild { Id = 0x7E57_2633u, Guid16 = Convert.FromHexString("d003e843ff6eee4d93467b13c0bd4fdd"), MasterCharId = Master };
        g.Ranks.Add(new GuildRank { Index = 0, Name = "Gatekeeper", Permissions = 0x0F });
        g.Ranks.Add(new GuildRank { Index = 1, Name = "Gatewarden", Permissions = 0x0F });
        g.Ranks.Add(new GuildRank { Index = 9, Name = "Recruit", Permissions = 0x00 });
        return g;
    }

 /// <summary>The request's fields as GuildModifyAuthorityHandler reads them.</summary>
    private static (byte Index, string Name, byte Perms) Parse1468(string hex)
    {
        var b = Convert.FromHexString(hex);
        Assert.Equal(40, b.Length);
        return (b[16], GuildPackets.ReadWideNameAt(b, 17, GuildRosterPackets.RankNameMaxChars), b[39]);
    }

    
    
    private const string Req1468Index9Gatekeeper = "d003e843ff6eee4d93467b13c0bd4fdd0947006100740065006b0065006500700065007200000000";
    
    private const string Req1468Index0Sovereign = "d003e843ff6eee4d93467b13c0bd4fdd0053006f007600650072006500690067006e00000000000f";

    [Fact]
    public void Ans2633_DuplicateTitle_IsRetailsCode20()
    {
        
        
        var g = GatekeeperGuild();
        var (index, name, perms) = Parse1468(Req1468Index9Gatekeeper);

        var refusal = GuildService.TryModifyRank(g, Master, index, name, perms, out var stored);

        Assert.Equal(GuildService.ModifyRankRefusal.DuplicateName, refusal);
        Assert.Null(stored);
        Assert.Equal("1400", Hex(GuildModifyAuthorityHandler.BuildAnsGuildModifyAuthority2633(GuildModifyAuthorityHandler.ResultOf(refusal))));
        Assert.Equal("Recruit", g.Ranks.Single(r => r.Index == 9).Name);   
    }

    [Fact]
    public void Nfy2637_AfterTheCollisionIsGone_EchoesTheRequestAndStores()
    {
        
        
        var g = GatekeeperGuild();
        var r0 = Parse1468(Req1468Index0Sovereign);
        Assert.Equal(GuildService.ModifyRankRefusal.None, GuildService.TryModifyRank(g, Master, r0.Index, r0.Name, r0.Perms, out _));

        var (index, name, perms) = Parse1468(Req1468Index9Gatekeeper);
        var refusal = GuildService.TryModifyRank(g, Master, index, name, perms, out var stored);

        Assert.Equal(GuildService.ModifyRankRefusal.None, refusal);
        Assert.Equal("0000", Hex(GuildModifyAuthorityHandler.BuildAnsGuildModifyAuthority2633(GuildModifyAuthorityHandler.ResultOf(refusal))));
        Assert.NotNull(stored);
        Assert.Equal("0947006100740065006b0065006500700065007200000000", Hex(GuildRosterPackets.BuildRankRecord(stored!)));
        Assert.Equal("Gatekeeper", g.Ranks.Single(r => r.Index == 9).Name);
    }

    [Fact]
    public void ModifyRank_TheSameIndexUnderItsOwnName_IsNoDuplicate()
    {
        
        var g = GatekeeperGuild();
        Assert.Equal(GuildService.ModifyRankRefusal.None, GuildService.TryModifyRank(g, Master, 0, "Gatekeeper", 0x0F, out _));
    }

    [Fact]
    public void ModifyRank_ComparesTheExactText()
    {
        
        var g = GatekeeperGuild();
        Assert.Equal(GuildService.ModifyRankRefusal.None, GuildService.TryModifyRank(g, Master, 1, "Gatekeeper‎", 0x0F, out _));
    }

    [Theory]
    [InlineData(Master + 1, (byte)3, GuildModifyAuthorityHandler.ResultNotMaster)]          
    [InlineData(Master, (byte)GuildService.MaxRanks, GuildModifyAuthorityHandler.ResultOutOfRange)] 
    public void ModifyRank_OtherRefusals_AreTheClientsOwnCodes(uint requester, byte index, ushort code)
    {
        var g = GatekeeperGuild();
        var refusal = GuildService.TryModifyRank(g, requester, index, "Champion", 0x0D, out var stored);
        Assert.Null(stored);
        Assert.Equal(code, GuildModifyAuthorityHandler.ResultOf(refusal));
        Assert.Equal(3, g.Ranks.Count);
    }

    [Fact]
    public void ResultCodes_AreTheGuildResultSwitchCases()
    {
        
        
        
        Assert.Equal(GuildDelAuthorityHandler.ResultGuildNotFound, GuildModifyAuthorityHandler.ResultGuildNotFound);
        Assert.Equal(GuildDelAuthorityHandler.ResultNotMaster, GuildModifyAuthorityHandler.ResultNotMaster);
        Assert.Equal(GuildDelAuthorityHandler.ResultOutOfRange, GuildModifyAuthorityHandler.ResultOutOfRange);
        Assert.Equal((ushort)20, GuildModifyAuthorityHandler.ResultDuplicateName);
        Assert.Equal(GuildModifyAuthorityHandler.ResultGuildNotFound,
            GuildModifyAuthorityHandler.ResultOf(GuildService.ModifyRankRefusal.NotRegistered));
    }

    [Fact]
    public void HoldersOf_AreTheMembersOnTheEditedTitle()
    {
        
        
        var g = GatekeeperGuild();
        g.Members.Add(new GuildMember { CharId = Master, AuthId = 0 });
        g.Members.Add(new GuildMember { CharId = 11, AuthId = 9 });
        g.Members.Add(new GuildMember { CharId = 12, AuthId = 1 });
        g.Members.Add(new GuildMember { CharId = 13, AuthId = 9 });

        Assert.Equal(new uint[] { 11, 13 }, GuildService.HoldersOf(g, 9));
        Assert.Equal(new[] { Master }, GuildService.HoldersOf(g, 0));
        Assert.Empty(GuildService.HoldersOf(g, 5));
    }

    [Fact]
    public void PcViewInfo_OfAHolder_CarriesTheRenamedTitle()
    {
        
        
        var g = GatekeeperGuild();
        g.Members.Add(new GuildMember { CharId = 11, AuthId = 9 });
        Assert.Equal(GuildService.ModifyRankRefusal.None, GuildService.TryModifyRank(g, Master, 9, "Wild Card", 0x0D, out _));

        var body = PcViewInfoPackets.BuildNfyPcViewInfoUpdate(0x09D92211u, g, 11, TestEmblems.Create());
        Assert.Equal("Wild Card", GuildPackets.ReadWideNameAt(body, 153, GuildRosterPackets.RankNameMaxChars));
    }
}
