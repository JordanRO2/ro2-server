using RO2.Persistence.Mongo.Persistence;
using RO2.Persistence.Mongo.Repositories;
using System.Buffers.Binary;
using System.Text.Json;
using RO2.Domain.Entities;
using RO2.GameData.Tables;
using RO2.Network.Packets;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// The guild invite and departure flow: 2565 AnsGuildInviteMember (including the code-73 rejoin-cooldown date),
/// 2566 NfyGuildInviteYouReq, 2569 AnsGuildKickMember, both forms of 2570 NfyGuildKickMember, 2645
/// NfyGuildMasterHandover, the 1429 decision rules and the member GID.
/// </summary>
public class UnitA0GuildInviteDepartureTests
{
    private static string Hex(ReadOnlySpan<byte> b) => Convert.ToHexString(b).ToLowerInvariant();

 /// <summary>Number of bytes up to and including the NUL of the UTF-16 name at <paramref name="off"/>.</summary>
    private static int NameSpan(ReadOnlySpan<byte> b, int off)
    {
        var i = off;
        while (b[i] != 0 || b[i + 1] != 0) i += 2;
        return i + 2 - off;
    }

    private static Guild GuildWith(string guidHex, string name) => new()
    {
        Id = 1, Guid16 = Convert.FromHexString(guidHex), Name = name,
    };

    
    [Fact]
    public void Ans2565_NoSuchName_MatchesRetailByteForByte()
    {
        
        
        const string retail = "360064006f0075006600660065007200730074007200610066006500720000000000000000000000000000000000000000000000000000000000ffffffff";
        var ours = GuildPackets.BuildAnsGuildInviteMember(GuildInviteRules.NoSuchName, "doufferstrafer", null);
        Assert.Equal(retail, Hex(ours));
    }

    [Fact]
    public void Ans2565_Sent_MatchesRetailByteForByte()
    {
        
        const string retail = "00004e0061006d0061006b00750072006f0000000000000000000000000000000000000000000000000000000000000000000000000000000000ffffffff";
        Assert.Equal(retail, Hex(GuildPackets.BuildAnsGuildInviteMember(GuildInviteRules.Sent, "Namakuro", null)));
    }

    [Theory]
    
    
    [InlineData("49005300650063007200650074004800650076006e0053007000790000000000a7c49fccf77f0000b0f3dc1b0000a032fe95ac99e64000000000ac99e640", "SecretHevnSpy")]
    
    [InlineData("4900540065006d00700065007300740055007300650072000000b50f00000000a7c49fccf77f0000b0f3dc1b000092d8b49d319ae64000000000319ae640", "TempestUser")]
    
    [InlineData("49004e0069006e0079006d000000000076cf71eff67f00002018101500000000a7c452eff67f000010f1131c0000eac9ad1ff490e64000000000f490e640", "Ninym")]
    public void Ans2565_RejoinCooldown_CarriesTheEndDate(string retailHex, string typed)
    {
        
        
        
        var retail = Convert.FromHexString(retailHex);
        var end = DateTime.FromOADate(BinaryPrimitives.ReadDoubleLittleEndian(retail.AsSpan(46)));
        var ours = GuildPackets.BuildAnsGuildInviteMember(GuildInviteRules.RejoinCooldownActive, typed, end);

        Assert.Equal(62, ours.Length);
        var named = 2 + NameSpan(retail, 2);
        Assert.Equal(Hex(retail.AsSpan(0, named)), Hex(ours.AsSpan(0, named)));
        Assert.Equal(Hex(retail.AsSpan(46, 16)), Hex(ours.AsSpan(46, 16)));
        Assert.All(ours.AsSpan(named, 46 - named).ToArray(), b => Assert.Equal(0, b));
    }

    [Theory]
    
    [InlineData("0b0041004b00420073006d006900740068000000000000000000000000000000824e84e0f67f000098f0131c0000000000000000000000000000ffffffff", "AKBsmith", GuildInviteRules.AlreadyInvited)]
    
    [InlineData("08006e00790065006e00790065006e00790065006e0079006500000000000000824e84e0f67f000000b39e0f0000000000000000000000000000ffffffff", "nyenyenyenye", GuildInviteRules.InviterNoRight)]
    public void Ans2565_OtherRefusals_CarryTheNullTail(string retailHex, string typed, ushort code)
    {
        var retail = Convert.FromHexString(retailHex);
        var ours = GuildPackets.BuildAnsGuildInviteMember(code, typed, null);
        var named = 2 + NameSpan(retail, 2);
        Assert.Equal(Hex(retail.AsSpan(0, named)), Hex(ours.AsSpan(0, named)));
        Assert.Equal(Hex(retail.AsSpan(46, 16)), Hex(ours.AsSpan(46, 16)));
        Assert.All(ours.AsSpan(named, 46 - named).ToArray(), b => Assert.Equal(0, b));
    }

    
    [Fact]
    public void Nfy2566_MatchesRetailOnEveryFieldTheClientReads()
    {
        
        
        
        var retail = Convert.FromHexString(
            "de0000002dfbcba076b8434a815d7f4cf2b6c68ac29aeb68e321924f8895c81c595e1ba24400650061006c0062007200650061006b0065007200000000000000000000007078960200000000755cc4ccf77f000060e4a40000000000707896020000000068f7dc1b000000003d80827ef97f0000feffffffffffffff70789602000000000100000048004500520045003200480045004c0050000000d3f8a5ccf77f000070f7dc1b0000000068f7dc1b000000004e0061006d0061006b00750072006f000000610f000000001ef9a5ccf77f00000000000000000000000000000000");
        var guild = GuildWith("2dfbcba076b8434a815d7f4cf2b6c68a", "Dealbreaker");
        var token = retail.AsSpan(20, 16).ToArray();
        var ours = GuildPackets.BuildNfyGuildInviteYouReq(guild, token, "HERE2HELP", "Namakuro");

        Assert.Equal(226, ours.Length);
        Assert.Equal(Hex(retail.AsSpan(0, 36)), Hex(ours.AsSpan(0, 36)));      
        var mask = new bool[226];
        for (var i = 0; i < 36; i++) mask[i] = true;
        foreach (var off in new[] { 36, 136, 180 })
        {
            var n = NameSpan(retail, off);
            Assert.Equal(Hex(retail.AsSpan(off, n)), Hex(ours.AsSpan(off, n)));
            for (var i = off; i < off + n; i++) mask[i] = true;
        }
        Assert.Equal("0000", Hex(retail.AsSpan(224, 2)));                       
        Assert.Equal("0000", Hex(ours.AsSpan(224, 2)));
        for (var i = 0; i < 226; i++)
            if (!mask[i]) Assert.Equal(0, ours[i]);
    }

    [Fact]
    public void Nfy2566_InviteeField_IsTheTypedName()
    {
        
        var ours = GuildPackets.BuildNfyGuildInviteYouReq(GuildWith("00112233445566778899aabbccddeeff", "G"),
            new byte[16], "Inviter", "irocrecentia");
        Assert.Equal("irocrecentia", GuildPackets.ReadWideNameAt(ours, 180, 22));
    }

    
    [Fact]
    public void Ans2569_Success_MatchesRetailByteForByte()
    {
        
        
        var request = Convert.FromHexString("2e00000044006500610063006f006e0042006c00610063006b000000000000000000000020ea1a00f0e91a006f5a976c0000");
        const string retail = "400000000000d003e843ff6eee4d93467b13c0bd4fdd44006500610063006f006e0042006c00610063006b000000000000000000000020ea1a00f0e91a006f5a976c0000";
        var guild = GuildWith("d003e843ff6eee4d93467b13c0bd4fdd", "Heaven's Gate");
        Assert.Equal(retail, Hex(GuildKickHandler.BuildAnsGuildKickMember(0, guild, request, "DeaconBlack")));
    }

    [Fact]
    public void Ans2569_Refusal_ZeroesTheGuidAndEchoesTheName()
    {
        
        
        
        var request = Convert.FromHexString("2e0000004d006f006e006500640075006c00610000001a0024ea1a002cd1f7fbfeffffff20eb1a007ce81a008392a1750000");
        const string retail = "400000003d00000000000000000000000000000000004d006f006e006500640075006c00610000001a0024ea1a002cd1f7fbfeffffff20eb1a007ce81a008392a1750000";
        var guild = GuildWith("d003e843ff6eee4d93467b13c0bd4fdd", "Heaven's Gate");
        Assert.Equal(retail, Hex(GuildKickHandler.BuildAnsGuildKickMember(61, guild, request, "Monedula")));

        var ours = GuildKickHandler.BuildAnsGuildKickMember(GuildKickHandler.ResultNoSuchMember, guild, request, "Monedula");
        Assert.Equal("0a00" + new string('0', 32), Hex(ours.AsSpan(4, 18)));
    }

    [Fact]
    public void Ans2569_RefusalCodes_AreTheClientsOwn()
    {
        
        
        
        Assert.Equal(7, GuildKickHandler.ResultNoGuild);
        Assert.Equal(8, GuildKickHandler.ResultNoRight);
        Assert.Equal(10, GuildKickHandler.ResultNoSuchMember);
        Assert.Equal(24, GuildKickHandler.ResultSelf);
    }

    [Fact]
    public void Nfy2570_OnlineForm_MatchesRetail()
    {
        
        
        Assert.Equal("1d0000000d480065006100760065006e0027007300200047006100740065000000",
            Hex(GuildKickHandler.BuildNfyGuildKickMember("Heaven's Gate")));
    }

    [Fact]
    public void Nfy2570_OfflineForm_IsRetailsConstant()
    {
        
        
        
        Assert.Equal("050000000112000000", Hex(GuildDepartureRepository.KickNoticeBody()));
    }

    [Fact]
    public async Task DepartureStore_KickNotice_IsTakenOnce()
    {
        const uint charId = 0xA0A0_0001;
        var departures = new GuildDepartureRepository(new MongoDbContext("mongodb://127.0.0.1:1"));
        Assert.False(await departures.TakeKickNoticeAsync(charId));
        await departures.MarkKickNoticeAsync(charId);
        Assert.True(await departures.TakeKickNoticeAsync(charId));
        Assert.False(await departures.TakeKickNoticeAsync(charId));
    }

    [Fact]
    public async Task DepartureStore_RecordsTheLeave()
    {
        const uint charId = 0xA0A0_0002;
        var left = new DateTime(2026, 9, 20, 15, 26, 23, 500, DateTimeKind.Utc);
        var departures = new GuildDepartureRepository(new MongoDbContext("mongodb://127.0.0.1:1"));
        await departures.RecordLeaveAsync(charId, left);
        Assert.Equal(left, departures.LeftUtcOf(charId));
        
        await departures.MarkKickNoticeAsync(charId);
        Assert.Equal(left, departures.LeftUtcOf(charId));
        Assert.True(await departures.TakeKickNoticeAsync(charId));
    }

    
    private static GuildInviteRules.InviteFacts AllClear => new(
        InviterInGuild: true, InviterMayInvite: true, NameExists: true, InviteeOnline: true,
        InviteeInGuild: false, RejoinAtUtc: null, InviteePending: false);

    [Fact]
    public void Decide_FollowsTheInviterToInviteeOrder()
    {
        var end = new DateTime(2026, 9, 20, 16, 26, 23, DateTimeKind.Utc);
        Assert.Equal((GuildInviteRules.Sent, (DateTime?)null), GuildInviteRules.Decide(AllClear));
        Assert.Equal(GuildInviteRules.InviterNoGuild, GuildInviteRules.Decide(AllClear with { InviterInGuild = false, InviterMayInvite = false }).Code);
        Assert.Equal(GuildInviteRules.InviterNoRight, GuildInviteRules.Decide(AllClear with { InviterMayInvite = false, NameExists = false }).Code);
        Assert.Equal(GuildInviteRules.NoSuchName, GuildInviteRules.Decide(AllClear with { NameExists = false, InviteeOnline = false }).Code);
        Assert.Equal(GuildInviteRules.InviteeOffline, GuildInviteRules.Decide(AllClear with { InviteeOnline = false }).Code);
        Assert.Equal(GuildInviteRules.InviteeInGuild, GuildInviteRules.Decide(AllClear with { InviteeInGuild = true, InviteePending = true }).Code);
        Assert.Equal((GuildInviteRules.RejoinCooldownActive, (DateTime?)end),
            GuildInviteRules.Decide(AllClear with { RejoinAtUtc = end, InviteePending = true }));
        Assert.Equal(GuildInviteRules.AlreadyInvited, GuildInviteRules.Decide(AllClear with { InviteePending = true }).Code);
    }

    [Fact]
    public void RejoinCooldown_RunsOneHourFromTheLeave()
    {
        
        var left = new DateTime(2026, 9, 20, 15, 26, 23, 500, DateTimeKind.Utc);
        var hour = TimeSpan.FromHours(1);
        var at = new DateTime(2026, 9, 20, 16, 22, 8, 486, DateTimeKind.Utc);
        Assert.Equal(left + hour, GuildInviteRules.RejoinAtUtc(left, hour, at));
        Assert.Null(GuildInviteRules.RejoinAtUtc(left, hour, left + hour));
        Assert.Null(GuildInviteRules.RejoinAtUtc(null, hour, at));
        Assert.Null(GuildInviteRules.RejoinAtUtc(left, TimeSpan.Zero, at));
    }

    [Fact]
    public void PendingInvite_BlocksForTheLifetime()
    {
        
        var t0 = new DateTime(2026, 8, 9, 19, 5, 12, DateTimeKind.Utc);
        var invite = new GuildPendingInvite { CreatedUtc = t0 };
        var life = TimeSpan.FromSeconds(300);
        Assert.True(GuildInviteRules.IsPending(invite, life, t0.AddSeconds(65)));
        Assert.True(GuildInviteRules.IsPending(invite, life, t0.AddSeconds(69)));
        Assert.False(GuildInviteRules.IsPending(invite, life, t0.AddSeconds(332)));
        Assert.False(GuildInviteRules.IsPending(null, life, t0));
    }

    [Fact]
    public void TryAddInvite_RefusesWhileTheInviteeHoldsOne()
    {
        const uint invitee = 0xA0A0_0010;
        var t0 = new DateTime(2026, 8, 9, 19, 5, 12, DateTimeKind.Utc);
        var life = TimeSpan.FromSeconds(300);
        var svc = GuildService.Instance;
        Assert.True(svc.TryAddInvite(invitee, 1, 2, new byte[16], t0, life));
        Assert.False(svc.TryAddInvite(invitee, 3, 4, new byte[16], t0.AddSeconds(65), life));
        Assert.Equal(1u, svc.PeekInvite(invitee)!.GuildId);
        Assert.True(svc.TryAddInvite(invitee, 3, 4, new byte[16], t0.AddSeconds(332), life));
        Assert.Equal(3u, svc.TakeInvite(invitee)!.GuildId);
        Assert.Null(svc.PeekInvite(invitee));
    }

    [Fact]
    public void ConfigDef_GivesOneHourAndFiveMinutes()
    {
        var config = JsonSerializer.Deserialize<List<ConfigDef>>(File.ReadAllText(Path.Combine(DataDirectory(), "Config_Def.json")))!;
        Assert.Equal(TimeSpan.FromHours(1), GuildInviteRules.RejoinCooldown(config));
        Assert.Equal(TimeSpan.FromSeconds(300), GuildInviteRules.InviteLifetime(config));
        Assert.Equal(TimeSpan.Zero, GuildInviteRules.RejoinCooldown(Array.Empty<ConfigDef>()));
    }

    private static string DataDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "data");
            if (File.Exists(Path.Combine(candidate, "Config_Def.json"))) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("no ancestor of " + AppContext.BaseDirectory + " holds data/Config_Def.json");
    }

    
    [Fact]
    public void GidFor_IsTheCharacterListUid()
    {
        
        var character = new Character { Id = 4242, Name = "Tester" };
        using var w = new PacketWriter(256);
        CharacterSerializer.WriteCharacterList(w, 7, new[] { character });
        var body = w.ToArray();
        
        Assert.Equal(GuildService.GidFor(4242), BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(10)));
    }

    [Fact]
    public void NormalizeGids_RepairsTheOldBand()
    {
        var g = new Guild { Id = 9 };
        g.Members.Add(new GuildMember { CharId = 5, Gid = 1_000_000_000_000_000_005UL });
        g.Members.Add(new GuildMember { CharId = 6, Gid = GuildService.GidFor(6) });
        Assert.True(GuildService.NormalizeGids(g));
        Assert.Equal(GuildService.GidFor(5), g.Members[0].Gid);
        Assert.False(GuildService.NormalizeGids(g));
    }

    [Fact]
    public void Nfy2645_MatchesRetail()
    {
        
        Assert.Equal("a87cb1a7b3b6e00d34209ba7b3b6e00d0009",
            Hex(GuildPackets.BuildNfyGuildMasterHandover(0x0DE0B6B3A7B17CA8UL, 0x0DE0B6B3A79B2034UL, 9)));
    }
}
