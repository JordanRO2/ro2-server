using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Party-matching whisper packets: 1462 ReqPartyMatchingWhisper and 2625 AnsPartyMatchingWhisper.
/// The tests pin the wire shapes used by the client's builder and reader.
/// </summary>
public class PartyMatchingWhisper2625Tests
{
    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    [Fact]
    public void Request_Is_Handle_Gid_Roles()
    {
        
        var body = Convert.FromHexString("15230100" + "07ccb8a7b3b6e00d" + "05");

        Assert.True(PartyMatchingWhisperPackets.TryParseRequest(body, out var r));
        Assert.Equal(new PartyMatchingWhisperPackets.Request(0x00012315u, 0x0DE0B6B3A7B8CC07UL, 5), r);
    }

    [Fact]
    public void Request_KeepsOnlyTheThreeRoleBits()
    {
        var body = Convert.FromHexString("00000000" + "0100000000000000" + "ff");

        Assert.True(PartyMatchingWhisperPackets.TryParseRequest(body, out var r));
        Assert.Equal(PartyMatchingWhisperPackets.RoleMask, r.Roles);
        Assert.False(PartyMatchingWhisperPackets.TryParseRequest(body.AsSpan(0, 12), out _));
    }

 /// <summary>
 /// 52-byte body: +4 level, +6 class, +8 roles, +9 master level, +10 wchar_t[21] name. Offsets +0..3, +5 and +7
 /// are never read and are zero.
 /// </summary>
    [Fact]
    public void Answer_MatchesReaderLayout_ByteForByte()
    {
        var expected =
            "00000000" + "32" + "00" + "0d" + "00"   
            + "05" + "00"                           
            + "41007a0075007600" + new string('0', 68); 

        var body = PartyMatchingWhisperPackets.BuildAnsPartyMatchingWhisper(
            new PartyMatchingDetailPackets.Member(50, 13, 0, "Azuv"),
            PartyMatchingWhisperPackets.RoleTank | PartyMatchingWhisperPackets.RoleHealer);

        Assert.Equal(PartyMatchingWhisperPackets.AnswerBytes, body.Length);
        Assert.Equal(expected, Hex(body));
    }

    [Fact]
    public void Answer_MasterLevelAndLongName()
    {
        var body = PartyMatchingWhisperPackets.BuildAnsPartyMatchingWhisper(
            new PartyMatchingDetailPackets.Member(50, 13, 40, new string('y', 30)), PartyMatchingWhisperPackets.RoleDealer);

        Assert.Equal(52, body.Length);
        Assert.Equal(PartyMatchingWhisperPackets.RoleDealer, body[8]);
        Assert.Equal(40, body[9]);                    
        Assert.Equal((byte)'y', body[10 + 19 * 2]);   
        Assert.Equal(0, body[10 + 20 * 2]);           
        Assert.Equal(0, body[51]);
    }

    [Fact]
    public void Refusal_OnlyForAnApplicantInAParty()
    {
        var mine = new PartyState { PartyId = 3 };
        var theirs = new PartyState { PartyId = 4 };

        Assert.Equal(0, PartyMatchingWhisperPackets.RefusalCode(null, theirs));
        Assert.Equal(0, PartyMatchingWhisperPackets.RefusalCode(null, null));
        Assert.Equal(PartyMatchingDetailPackets.CodeAlreadyInParty,
            PartyMatchingWhisperPackets.RefusalCode(theirs, new PartyState { PartyId = 4 }));
        Assert.Equal(PartyMatchingDetailPackets.CodeInAnotherParty, PartyMatchingWhisperPackets.RefusalCode(mine, theirs));
        Assert.Equal(PartyMatchingDetailPackets.CodeInAnotherParty, PartyMatchingWhisperPackets.RefusalCode(mine, null));
    }
}
