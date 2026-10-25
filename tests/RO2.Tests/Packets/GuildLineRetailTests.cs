using System.Buffers.Binary;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>Guild line record carried by 2559, 2560 and 2591: the Basic Robe record, its placement in 2559 and 2591, the 115-byte roster record and the roster order.</summary>
public class GuildLineRetailTests
{
 /// <summary>Basic Robe OLE date, passed explicitly as the Unix epoch in the server's local time zone, so the tests do not depend on the machine's time zone.</summary>
    private static readonly double RetailBasicRobeDate =
        BitConverter.ToDouble(Convert.FromHexString("abaaaaaa2af8d840"));

    private static string Hex(ReadOnlySpan<byte> b) => Convert.ToHexString(b).ToLowerInvariant();

    [Fact]
    public void BasicRobe_InfoForm_MatchesRetail2559()
    {
        
        
        
        
        var rec = new byte[25];
        GuildRobeRecord.WriteInfoForm(rec, RetailBasicRobeDate);
        Assert.Equal("24f6c100abaaaaaa2af8d8400000000000000000" + "81af9fff80", Hex(rec));
        Assert.Equal(25568.666666666668, RetailBasicRobeDate);
    }

    [Fact]
    public void BasicRobe_ViewForm_MatchesRetail2591()
    {
        
        
        
        var rec = new byte[24];
        GuildRobeRecord.WriteViewForm(rec, RetailBasicRobeDate);
        Assert.Equal("24f6c10081af9fffabaaaaaa2af8d8400000000000000000", Hex(rec));
    }

    [Fact]
    public async Task AnsMyGuildInfo_Object1_MatchesRetail()
    {
        
        
        var guid = Convert.FromHexString("de83b20dbc5a164ea2303ca75b474951");
        var emblems = TestEmblems.Create();
        var emblem = await emblems.SetAsync(guid, new byte[] { 1 });   
        Assert.Equal((ushort)1, emblem.Index);

        var g = new Guild { Id = 0x7E57_0001, Guid16 = guid, Name = "LeSserafim", MasterCharId = 1 };
        g.Members.Add(new GuildMember { CharId = 1, Gid = 1, Name = "Master", AuthId = 0 });
        // This test pins only Object 1 (identity + robe), which is independent of the guild level, so an empty
        // exp table (guild level 0) is sufficient here; the level path is covered by the exp-table unit tests.
        var body = GuildRosterPackets.BuildAnsMyGuildInfo(g, 1, _ => false, emblems, Array.Empty<RO2.GameData.Tables.GuildExpTable>());

        const string retailHead = "40000000" + "de83b20dbc5a164ea2303ca75b474951" + "0a"
            + "4c00650053007300650072006100660069006d00" + "0100";
        int robeOff = retailHead.Length / 2;
        Assert.Equal(retailHead, Hex(body.AsSpan(0, robeOff)));
        
        
        Assert.Equal(Hex(GuildRobeRecord.InfoForm()), Hex(body.AsSpan(robeOff, 25)));
        
        Assert.Equal(4 + 0x40, robeOff + 25);
    }

    [Fact]
    public void NfyPcViewInfoUpdate_CarriesTheRobeAt128()
    {
        var emblems = TestEmblems.Create();
        var g = new Guild { Id = 0x7E57_0002, Guid16 = Convert.FromHexString("00112233445566778899aabbccddeeff"), Name = "G" };
        var body = PcViewInfoPackets.BuildNfyPcViewInfoUpdate(0x0b494011, g, 42, emblems);
        Assert.Equal(177, body.Length);
        Assert.Equal(Hex(GuildRobeRecord.ViewForm()), Hex(body.AsSpan(128, 24)));
        Assert.Equal(1, body[152]);

        
        var none = PcViewInfoPackets.BuildNfyPcViewInfoUpdate(0x0b494011, null, 42, emblems);
        Assert.True(none.AsSpan(128, 24).IndexOfAnyExcept((byte)0) < 0);
    }

    [Fact]
    public void MemberRecord_MatchesRetailRosterRecord()
    {
        
        
        
        
        const string retail =
            "8df1eca7b3b6e00d0000000000000000000000000032000a000200320000c84803000000000079eb9a14137de64000000000137de640"
            + "012804b0040000000000000000000000000000"
            + "5200650065006b0073004f0066004600610069006c007500720065000000000000000000000000000000";
        var expected = Convert.FromHexString(retail);
        Assert.Equal(GuildRosterPackets.MemberRecordLen, expected.Length);

        double retailJoin = BinaryPrimitives.ReadDoubleLittleEndian(expected.AsSpan(38));
        var member = new GuildMember
        {
            Gid = 1000000000008974733UL,
            Name = "ReeksOfFailure",
            AuthId = 1,
            ContribPoints = 215240,
            JoinedUtc = DateTime.SpecifyKind(DateTime.FromOADate(retailJoin), DateTimeKind.Local).ToUniversalTime(),
        };
        var view = new GuildRosterPackets.MemberView(member, Level: 50, Class: 10, Profession: 2, JobLevel: 50,
            ProJobLevel: 40, ProJobId: 4, MapId: 1200);
        var rec = GuildRosterPackets.BuildMemberRecord(view);

        
        
        Assert.Equal(Hex(expected.AsSpan(0, 38)), Hex(rec.AsSpan(0, 38)));
        Assert.Equal(Hex(expected.AsSpan(46)), Hex(rec.AsSpan(46)));
        Assert.InRange(Math.Abs(BinaryPrimitives.ReadDoubleLittleEndian(rec.AsSpan(38)) - retailJoin), 0, 1.0 / 86_400_000);
    }

    [Fact]
    public void Roster_IsInAscendingGidOrder()
    {
        
        
        var g = new Guild { Id = 0x7E57_0003, Guid16 = new byte[16], Name = "G" };
        var views = new[] { 3000UL, 1000UL, 2000UL }
            .Select(gid => new GuildRosterPackets.MemberView(new GuildMember { Gid = gid, Name = "m" + gid },
                1, 1, 1, 1, 0, 0, 0))
            .ToList();
        var body = GuildRosterPackets.BuildAnsGuildMemList(g, views);
        Assert.Equal(3, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(20)));
        var gids = Enumerable.Range(0, 3)
            .Select(i => BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(22 + i * GuildRosterPackets.MemberRecordLen)))
            .ToArray();
        Assert.Equal(new[] { 1000UL, 2000UL, 3000UL }, gids);
        Assert.Equal((uint)(body.Length - 4), BinaryPrimitives.ReadUInt32LittleEndian(body));
    }
}
