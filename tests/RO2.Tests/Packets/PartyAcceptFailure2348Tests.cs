using System.Buffers.Binary;
using System.Text;
using RO2.Network.Protocol;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// 2348 OnPartyError code 15708 ("Can't find the target player.") for an accepted invitation that no longer leads
/// to a party: the inviter's party has ended, or the inviter has left the game. Only the responder is answered and
/// no party is founded. An invitation sent with no party is not affected: it joins the inviter's party at accept time.
/// </summary>
public class PartyAcceptFailure2348Tests
{
    
    
    
    
    
    private const string TsuyoiFokaAccept1383 =
        "013c3c001ee311dc120300004cab56ab8f3c296f540065006d00700065007300740055007300650072000000000000000000000000000000000000000000";
    private const string TsuyoiFoka2348 =
        "013535001fe311dc12035c3d00540065006d00700065007300740055007300650072000000000000000000000000000000000000000000";
    
    private const string NYuruzuAccept1383 =
        "013c3c001ee3113e120300004cab56ab8f3c296f540065006d00700065007300740055007300650072000000000000000000000000000000000000000000";
    private const string NYuruzu2348 =
        "013535001fe3113e12035c3d00540065006d00700065007300740055007300650072000000000000000000000000000000000000000000";

    [Theory]
    [InlineData(TsuyoiFokaAccept1383, TsuyoiFoka2348)]
    [InlineData(NYuruzuAccept1383, NYuruzu2348)]
    public void InviterNotFound_2348_IsRetailsFrame(string requestHex, string answerHex)
    {
        var request = Convert.FromHexString(requestHex);
        var handle = BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(6));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(request.AsSpan(10))); 
        var name = Encoding.Unicode.GetString(request, 20, 42).TrimEnd('\0');
        Assert.Equal("TempestUser", name);
        Assert.Equal(Convert.FromHexString(answerHex), PartyStatePackets.BuildOnPartyError(handle, 15708, name));
    }

 /// <summary>
 /// The dialog went out from party {A, B}; B leaves, so the party ends. C's accept then gets 15708, founds no new
 /// party, and A receives nothing.
 /// </summary>
    [Fact]
    public async Task AcceptAfterTheInvitationsPartyEnded_Gets15708_AndFoundsNoParty()
    {
        await using var rig = await PartyWireRig.StartAsync(3);
        var inviter = rig.AddPlayer(0, "TempestUser");
        var mate = rig.AddPlayer(1, "Namazuko");
        var responder = rig.AddPlayer(2, "TsuyoiFoka");
        rig.TrackParty(0x0409, [inviter, mate]); 
        await rig.Party.InviteToPartyAsync(inviter, responder, "TsuyoiFoka");
        await rig.Party.LeavePartyAsync(mate.SessionId, PartyLeaveReason.Left);
        Assert.Null(rig.Party.GetPlayerParty(inviter.SessionId));
        await rig.DrainAsync();

        await rig.Party.AnswerInviteAsync(responder, rig.Handle(2), 0, inviter.CharacterUid, "TempestUser");

        Assert.Equal(new[] { PartyWireRig.Frame(ZoneS2COpcodes.OnPartyError,
            PartyStatePackets.BuildOnPartyError(rig.Handle(2), 15708, "TempestUser")) }, await rig.FramesAsync(2));
        Assert.Empty(await rig.FramesAsync(0));
        Assert.Null(rig.Party.GetPlayerParty(inviter.SessionId));
        Assert.Null(rig.Party.GetPlayerParty(responder.SessionId));

        
        await rig.Party.InviteToPartyAsync(inviter, responder, "TsuyoiFoka");
        await rig.Party.AnswerInviteAsync(responder, rig.Handle(2), 0, inviter.CharacterUid, "TempestUser");
        var formed = rig.Party.GetPlayerParty(inviter.SessionId);
        Assert.NotNull(formed);
        Assert.Same(formed, rig.Party.GetPlayerParty(responder.SessionId));
    }

 /// <summary>The inviter has left the game: the accept gets 15708, and a decline in the same state gets nothing.</summary>
    [Fact]
    public async Task AcceptOfAnOfflineInvitersDialog_Gets15708()
    {
        await using var rig = await PartyWireRig.StartAsync(3);
        var inviter = rig.AddPlayer(0, "Gone");
        var accepter = rig.AddPlayer(1, "Accepter");
        var decliner = rig.AddPlayer(2, "Decliner");
        await rig.Party.InviteToPartyAsync(inviter, accepter, "Accepter");
        await rig.Party.InviteToPartyAsync(inviter, decliner, "Decliner");
        rig.Players.RemovePlayer(inviter.SessionId, out _);
        await rig.DrainAsync();

        await rig.Party.AnswerInviteAsync(accepter, rig.Handle(1), 0, inviter.CharacterUid, "Gone");
        await rig.Party.AnswerInviteAsync(decliner, rig.Handle(2), 1, inviter.CharacterUid, "Gone");

        Assert.Equal(new[] { PartyWireRig.Frame(ZoneS2COpcodes.OnPartyError,
            PartyStatePackets.BuildOnPartyError(rig.Handle(1), 15708, "Gone")) }, await rig.FramesAsync(1));
        Assert.Empty(await rig.FramesAsync(2));
        Assert.Empty(await rig.FramesAsync(0));
        Assert.Null(rig.Party.GetPlayerParty(accepter.SessionId));
    }

 /// <summary>
 /// Dialogs sent with no party: the first accept founds the party and the next one joins it, with no 2348.
 /// </summary>
    [Fact]
    public async Task InvitationSentWithNoParty_JoinsThePartyTheInviterFormedSince()
    {
        await using var rig = await PartyWireRig.StartAsync(3);
        var inviter = rig.AddPlayer(0, "CurseDarkness");
        var first = rig.AddPlayer(1, "EndOfMe");
        var second = rig.AddPlayer(2, "IBehindOfYouI");
        await rig.Party.InviteToPartyAsync(inviter, first, "EndOfMe");
        await rig.Party.InviteToPartyAsync(inviter, second, "IBehindOfYouI");
        await rig.Party.AnswerInviteAsync(first, rig.Handle(1), 0, inviter.CharacterUid, "CurseDarkness");
        var party = rig.Party.GetPlayerParty(inviter.SessionId);
        Assert.NotNull(party);
        await rig.DrainAsync();

        await rig.Party.AnswerInviteAsync(second, rig.Handle(2), 0, inviter.CharacterUid, "CurseDarkness");

        var joinerFrames = await rig.FramesAsync(2);
        Assert.Equal(new[] { PartyWireRig.Frame(ZoneS2COpcodes.OnPartyNotifyCreate, rig.Party.BuildRosterBody(party, create: true)) },
            joinerFrames);
        Assert.Equal(new[]
        {
            PartyWireRig.Frame(ZoneS2COpcodes.OnPartyNotifyJoin, rig.Party.BuildJoinDeltaBody(party, second)),
            PartyWireRig.Frame(ZoneS2COpcodes.OnPartyInvite,
                PartyStatePackets.BuildOnPartyInvite(rig.Handle(0), 0, "IBehindOfYouI", 0)),
        }, await rig.FramesAsync(0));
        Assert.Equal(3, party.MemberSessionIds.Count);
    }

 /// <summary>A decline still closes the dialog on both sides with 15715, the path the accept refusals now share.</summary>
    [Fact]
    public async Task Decline_StillAnswersBothSides()
    {
        await using var rig = await PartyWireRig.StartAsync(2);
        var inviter = rig.AddPlayer(0, "Inviter");
        var target = rig.AddPlayer(1, "Target");
        await rig.Party.InviteToPartyAsync(inviter, target, "target");
        await rig.DrainAsync();

        await rig.Party.AnswerInviteAsync(target, rig.Handle(1), 1, inviter.CharacterUid, "Inviter");

        Assert.Equal(new[] { PartyWireRig.Frame(ZoneS2COpcodes.OnPartyError,
            PartyStatePackets.BuildOnPartyError(rig.Handle(1), 15715, "Inviter")) }, await rig.FramesAsync(1));
        Assert.Equal(new[] { PartyWireRig.Frame(ZoneS2COpcodes.OnPartyInvite,
            PartyStatePackets.BuildOnPartyInvite(rig.Handle(0), 15715, "target", 0)) }, await rig.FramesAsync(0));
    }
}
