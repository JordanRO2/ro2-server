using System.Buffers.Binary;
using System.Text;
using RO2.Network.Protocol;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Refusals of a 1383 ReqPartyJoin accept that cannot be joined. The responder receives 2348 OnPartyError with the
/// reason and no state changes; the inviter is not notified. 15707 means the party filled up while the invitation
/// was open; 15710 means the responder is already in a party.
/// </summary>
public class PartyJoinRefusalTests
{
    
    
    private const string SameOldWarAccept1383 =
        "013c3c001ee311a0ec0200002fddcfa7b3b6e00d430075007200730065004400610072006b006e0065007300730000000000000000000000000000000000";
    private const string SameOldWarFull2348 =
        "013535001fe311a0ec025b3d00430075007200730065004400610072006b006e0065007300730000000000000000000000000000000000";

    
    
    private const string TempestUserAccept1383 =
        "013c3c001ee3119414030000c413680dc161a63754007300750079006f00690046006f006b00610000000000000000000000000000000000000000000000";
    private const string TempestUserInParty2348 =
        "013535001fe3119414035e3d0054007300750079006f00690046006f006b00610000000000000000000000000000000000000000000000";

 /// <summary>PartyJoinHandler's offsets: own handle body+6, answer body+10, inviter GID body+12, name body+20.</summary>
    private static (uint Handle, ushort Answer, ulong InviterGid, string InviterName) Parse1383(string hex)
    {
        var body = Convert.FromHexString(hex);
        Assert.Equal(62, body.Length);
        Assert.Equal(58142, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(4)));
        return (BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(6)), BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(10)),
            BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(12)), Encoding.Unicode.GetString(body, 20, 42).TrimEnd('\0'));
    }

    [Theory]
    [InlineData(SameOldWarAccept1383, SameOldWarFull2348, (short)15707, "CurseDarkness")]
    [InlineData(TempestUserAccept1383, TempestUserInParty2348, (short)15710, "TsuyoiFoka")]
    public void AcceptRefusal_2348_IsRetailsFrame(string requestHex, string answerHex, short code, string inviterName)
    {
        var (handle, answer, _, name) = Parse1383(requestHex);
        Assert.Equal(0, answer);
        Assert.Equal(inviterName, name);
        Assert.Equal(Convert.FromHexString(answerHex), PartyStatePackets.BuildOnPartyError(handle, code, name));
    }

 /// Two invitations are accepted into a four-member party with one free slot. The first accept joins; the second is
 /// refused with 15707 carrying its own handle and the name from its 1383. The inviter receives no 2346 for it, and
 /// the party stays at five members without the refused responder.
    [Fact]
    public async Task AcceptIntoAPartyThatFilledUp_Gets15707_AndTheInviterNothing()
    {
        await using var rig = await PartyWireRig.StartAsync(6);
        var members = Enumerable.Range(0, 4).Select(i => rig.AddPlayer(i, "Member" + i)).ToArray();
        var first = rig.AddPlayer(4, "First");
        var late = rig.AddPlayer(5, "Late");
        var party = rig.TrackParty(0x61, members);
        await rig.Party.InviteToPartyAsync(members[0], first, "First");
        await rig.Party.InviteToPartyAsync(members[0], late, "Late");
        await rig.Party.AnswerInviteAsync(first, rig.Handle(4), 0, members[0].CharacterUid, "Member0");
        Assert.Equal(5, party.MemberSessionIds.Count);
        await rig.DrainAsync();

        await rig.Party.AnswerInviteAsync(late, rig.Handle(5), 0, members[0].CharacterUid, "Member0");

        Assert.Equal(new[] { PartyWireRig.Frame(ZoneS2COpcodes.OnPartyError,
            PartyStatePackets.BuildOnPartyError(rig.Handle(5), 15707, "Member0")) }, await rig.FramesAsync(5));
        for (int i = 0; i < 5; i++)
            Assert.Empty(await rig.FramesAsync(i));
        Assert.Equal(5, party.MemberSessionIds.Count);
        Assert.Null(rig.Party.GetPlayerParty(late.SessionId));

        
        await rig.Party.AnswerInviteAsync(late, rig.Handle(5), 0, members[0].CharacterUid, "Member0");
        Assert.Empty(await rig.FramesAsync(5));
    }

 /// The responder holds an open invitation from another party, forms its own party in the meantime, then accepts.
 /// It receives 2348 15710 and nothing is sent to the inviter; the responder stays in its own party.
    [Fact]
    public async Task AcceptWhileInAPartyAlready_Gets15710_AndTheInviterNothing()
    {
        await using var rig = await PartyWireRig.StartAsync(3);
        var inviter = rig.AddPlayer(0, "TsuyoiFoka");
        var responder = rig.AddPlayer(1, "TempestUser");
        var mate = rig.AddPlayer(2, "MiqueIla");
        await rig.Party.InviteToPartyAsync(inviter, responder, "TempestUser");
        await rig.Party.InviteToPartyAsync(responder, mate, "MiqueIla");
        await rig.Party.AnswerInviteAsync(mate, rig.Handle(2), 0, responder.CharacterUid, "TempestUser");
        var ownParty = rig.Party.GetPlayerParty(responder.SessionId);
        Assert.NotNull(ownParty);
        await rig.DrainAsync();

        await rig.Party.AnswerInviteAsync(responder, rig.Handle(1), 0, inviter.CharacterUid, "TsuyoiFoka");

        Assert.Equal(new[] { PartyWireRig.Frame(ZoneS2COpcodes.OnPartyError,
            PartyStatePackets.BuildOnPartyError(rig.Handle(1), 15710, "TsuyoiFoka")) }, await rig.FramesAsync(1));
        Assert.Empty(await rig.FramesAsync(0));
        Assert.Empty(await rig.FramesAsync(2));
        Assert.Same(ownParty, rig.Party.GetPlayerParty(responder.SessionId));
        Assert.Null(rig.Party.GetPlayerParty(inviter.SessionId));
    }
}
