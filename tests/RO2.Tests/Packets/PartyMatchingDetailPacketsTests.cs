using RO2.Domain.Entities;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Party-matching detail packets: 1461 ReqPartyMatchingDetail, 2623 AnsPartyMatchingDetail and
/// 2624 AnsPartyMatchingError. The tests pin the wire shapes used by the client's builder and readers.
/// </summary>
public class PartyMatchingDetailPacketsTests
{
    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    private const ulong Gid = 0x0DE0B6B3A7B8CC07UL;

    [Fact]
    public void Request_ReadsHandleThenGid()
    {
        
        var body = Convert.FromHexString("15230100" + "07ccb8a7b3b6e00d");

        Assert.True(PartyMatchingDetailPackets.TryParseRequest(body, out var handle, out var gid));
        Assert.Equal(0x00012315u, handle);
        Assert.Equal(Gid, gid);
    }

    [Fact]
    public void Request_ShorterThan12_IsRejected()
    {
        Assert.False(PartyMatchingDetailPackets.TryParseRequest(new byte[11], out _, out _));
    }

 /// <summary>
 /// One record: [compact 01 3e][u32 handle][u8 1][u64 GID], then the 49-byte record: +0 level, +2 class,
 /// +4 wchar_t[21] name, +46 master level, +48 mark 2 (hidden). Bytes +1, +3 and +47 are unused.
 /// </summary>
    [Fact]
    public void OneMember_MatchesReaderLayout_ByteForByte()
    {
        var expected =
            "013e"                                   
            + "15230100" + "01" + "07ccb8a7b3b6e00d" 
            + "32" + "00" + "0d" + "00"              
            + "41007a0075007600" + new string('0', 68) 
            + "00" + "00" + "02";                    

        var body = PartyMatchingDetailPackets.BuildAnsPartyMatchingDetail(0x00012315u, Gid,
            [new PartyMatchingDetailPackets.Member(50, 13, 0, "Azuv")]);

        Assert.Equal(expected, Hex(body));
    }

    [Fact]
    public void Records_Step49_AndCountIsBlobPlus4()
    {
        var members = new[]
        {
            new PartyMatchingDetailPackets.Member(50, 13, 40, "a"),
            new PartyMatchingDetailPackets.Member(12, 2, 0, "b"),
            new PartyMatchingDetailPackets.Member(30, 5, 0, "c"),
        };

        var body = PartyMatchingDetailPackets.BuildAnsPartyMatchingDetail(0, 1, members);

        
        Assert.Equal("02a000", Hex(body)[..6]);
        Assert.Equal(3 + 160, body.Length);
        var blob = body.AsSpan(3);
        Assert.Equal(3, blob[4]);
        for (var i = 0; i < members.Length; i++)
        {
            var rec = blob.Slice(PartyMatchingDetailPackets.AnswerHeaderBytes + i * PartyMatchingDetailPackets.MemberRecordBytes,
                PartyMatchingDetailPackets.MemberRecordBytes);
            Assert.Equal(members[i].Level, rec[0]);
            Assert.Equal(members[i].Job, rec[2]);
            Assert.Equal((byte)members[i].Name[0], rec[4]);
            Assert.Equal(members[i].MasterLevel, rec[46]);
            Assert.Equal(PartyMatchingDetailPackets.MarkHidden, rec[48]);
        }
    }

    [Fact]
    public void LongName_KeepsItsNul()
    {
        var body = PartyMatchingDetailPackets.BuildAnsPartyMatchingDetail(0, 1,
            [new PartyMatchingDetailPackets.Member(1, 1, 0, new string('x', 25))]);

        var rec = body.AsSpan(2 + PartyMatchingDetailPackets.AnswerHeaderBytes);
        Assert.Equal((byte)'x', rec[4 + 19 * 2]);   
        Assert.Equal(0, rec[4 + 20 * 2]);           
        Assert.Equal(0, rec[4 + 20 * 2 + 1]);
    }

    [Theory]
    [InlineData(PartyMatchingDetailPackets.ErrorOpDetail, PartyMatchingDetailPackets.CodeNoListing, "06006700")]
    [InlineData(PartyMatchingDetailPackets.ErrorOpApply, PartyMatchingDetailPackets.CodeNoListing, "07006700")]
    [InlineData(PartyMatchingDetailPackets.ErrorOpApply, PartyMatchingDetailPackets.CodeInAnotherParty, "07006900")]
    [InlineData(PartyMatchingDetailPackets.ErrorOpApply, PartyMatchingDetailPackets.CodeAlreadyInParty, "07006a00")]
    public void Error_IsOpThenCode(ushort op, ushort code, string expected)
    {
        
        Assert.Equal(expected, Hex(PartyMatchingDetailPackets.BuildError(op, code)));
    }

    [Fact]
    public void FindPoster_NeedsTheGidAndALivePosting()
    {
        var poster = new PlayerState { SessionId = 7, CharacterId = 70, CharacterUid = Gid, Name = "Azuv" };
        var other = new PlayerState { SessionId = 8, CharacterId = 80, CharacterUid = Gid + 1, Name = "Other" };
        var postings = new[] { new PartyMatchingBoard.Posting(70, 0, 0, 0, "t") };

        Assert.Same(poster, PartyMatchingDetailPackets.FindPoster([other, poster], postings, Gid));
        
        Assert.Null(PartyMatchingDetailPackets.FindPoster([other, poster], postings, Gid + 1));
        
        Assert.Null(PartyMatchingDetailPackets.FindPoster([other], postings, Gid));
        Assert.Null(PartyMatchingDetailPackets.FindPoster([poster], postings, 0));
    }

    [Fact]
    public void DetailMembers_SoloPosterAlone_PartyInRosterOrder()
    {
        var poster = new PlayerState { SessionId = 2, Name = "Poster" };
        Assert.Same(poster, Assert.Single(PartyMatchingDetailPackets.DetailMembers(poster, null, _ => null)));

        var a = new PlayerState { SessionId = 5, Name = "A" };
        var party = new PartyState { PartyId = 1, MemberSessionIds = [5, 9, 2] };
        var known = new Dictionary<uint, PlayerState> { [5] = a, [2] = poster };

        
        Assert.Equal(new[] { a, poster }, PartyMatchingDetailPackets.DetailMembers(poster, party, id => known.GetValueOrDefault(id)));
    }

    [Fact]
    public void MemberOf_UsesTheRosterSources()
    {
        
        
        var p = new PlayerState
        {
            Name = "Azuv", Level = 50, Job = 3,
            Character = new Character { Name = "Azuv", Level = 50, Job = 13, MasterLevel = 882 },
        };

        Assert.Equal(new PartyMatchingDetailPackets.Member(50, 13, 0, "Azuv"), PartyMatchingDetailPackets.MemberOf(p));

        
        var bare = new PlayerState { Name = "Bare", Level = 9, Job = 3 };
        Assert.Equal(new PartyMatchingDetailPackets.Member(9, 3, 0, "Bare"), PartyMatchingDetailPackets.MemberOf(bare));
    }
}
