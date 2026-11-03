using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Network.Protocol;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Characterization of the party decision chains (invite, answer, join, leave, succession, sub-master, frame move,
/// Helper) as the frames each client is sent and the party state left behind. The packet builders are pinned by the
/// retail-frame tests; this pins which frames go to whom and in what order, so the chains can move without drifting.
/// </summary>
public class PartyDecideChainsTests
{
    private static string Invite(PartyWireRig rig, int to, short code, string name, byte type = 0)
        => PartyWireRig.Frame(ZoneS2COpcodes.OnPartyInvite, PartyStatePackets.BuildOnPartyInvite(rig.Handle(to), code, name, type));

    private static string Roster(PartyWireRig rig, PartyState party, bool create = false)
        => PartyWireRig.Frame(create ? ZoneS2COpcodes.OnPartyNotifyCreate : ZoneS2COpcodes.OnPartyNotifyUpdate,
            rig.Party.BuildRosterBody(party, create));

    private static string Out2360(PartyState party, PlayerState who)
        => PartyWireRig.Frame(PartyNotifyOutPackets.Opcode, PartyNotifyOutPackets.BuildPartyNotifyOut2360(
            PartyAnswerPackets.PartyHandle(party), who.CharacterUid, PartyNotifyOutPackets.KindRemoved));

    private static string Master2365(PartyState party, PlayerState who)
        => PartyWireRig.Frame(ZoneS2COpcodes.OnPartyNotifyChangeMaster,
            PartyStatePackets.BuildOnPartyNotifyChangeMaster2365(PartyAnswerPackets.PartyHandle(party), who.CharacterUid));

    private static string Destroy2358(PartyState party)
        => PartyWireRig.Frame(PartyStatePackets.OpcodeOnPartyNotifyDestroy,
            PartyStatePackets.BuildOnPartyNotifyDestroy2358(PartyAnswerPackets.PartyHandle(party)));

    // ---- invite ----------------------------------------------------------------------------------------------

    /// <summary>Inviting oneself is dropped silently; inviting a member of another party is refused with 15710 to the inviter only.</summary>
    [Fact]
    public async Task Invite_SelfIsIgnored_AndATargetInAPartyIsRefusedWith15710()
    {
        await using var rig = await PartyWireRig.StartAsync(4);
        var a = rig.AddPlayer(0, "A");
        var b = rig.AddPlayer(1, "B");
        var c = rig.AddPlayer(2, "C");
        var d = rig.AddPlayer(3, "D");
        rig.TrackParty(0x61, [a, b]);
        rig.TrackParty(0x62, [c, d]);
        await rig.DrainAsync();

        await rig.Party.InviteToPartyAsync(a, a, "A");
        await rig.Party.InviteToPartyAsync(a, c, "c");

        Assert.Equal(new[] { Invite(rig, 0, 15710, "c") }, await rig.FramesAsync(0));
        Assert.Empty(await rig.FramesAsync(1));
        Assert.Empty(await rig.FramesAsync(2));
        Assert.Empty(await rig.FramesAsync(3));
    }

    /// <summary>
    /// A target with a dialog open refuses a second inviter with 15717 and gets no second dialog. The first invitation
    /// stays answerable.
    /// </summary>
    [Fact]
    public async Task Invite_WhileOneIsOpen_Gets15717_AndTheFirstStaysAnswerable()
    {
        await using var rig = await PartyWireRig.StartAsync(3);
        var a = rig.AddPlayer(0, "A");
        var b = rig.AddPlayer(1, "B");
        var t = rig.AddPlayer(2, "T");
        await rig.Party.InviteToPartyAsync(a, t, "T");
        Assert.Equal(new[] { PartyWireRig.Frame(ZoneS2COpcodes.OnPartyJoin,
            PartyStatePackets.BuildOnPartyJoin(rig.Handle(2), a.CharacterUid, 0, "A")) }, await rig.FramesAsync(2));
        await rig.DrainAsync();

        await rig.Party.InviteToPartyAsync(b, t, "t");

        Assert.Equal(new[] { Invite(rig, 1, 15717, "t") }, await rig.FramesAsync(1));
        Assert.Empty(await rig.FramesAsync(2));
        Assert.Empty(await rig.FramesAsync(0));

        await rig.Party.AnswerInviteAsync(t, rig.Handle(2), 0, a.CharacterUid, "A");
        Assert.NotNull(rig.Party.GetPlayerParty(t.SessionId));
        Assert.Equal(a.SessionId, rig.Party.GetPlayerParty(a.SessionId)!.LeaderSessionId);
    }

    // ---- answer ----------------------------------------------------------------------------------------------

    /// <summary>
    /// An answer with no open invitation, or naming another inviter's GID, is dropped and leaves the invitation open.
    /// An unknown answer code consumes the invitation without a frame; the invitation is then gone.
    /// </summary>
    [Fact]
    public async Task Answer_NoInvite_WrongGid_UnknownCode_AndTheCasConsumesTheInvitation()
    {
        await using var rig = await PartyWireRig.StartAsync(2);
        var a = rig.AddPlayer(0, "A");
        var t = rig.AddPlayer(1, "T");

        await rig.Party.AnswerInviteAsync(t, rig.Handle(1), 0, a.CharacterUid, "A");
        Assert.Null(rig.Party.GetPlayerParty(a.SessionId));

        await rig.Party.InviteToPartyAsync(a, t, "T");
        await rig.DrainAsync();
        await rig.Party.AnswerInviteAsync(t, rig.Handle(1), 0, a.CharacterUid + 1, "A");
        Assert.Null(rig.Party.GetPlayerParty(a.SessionId));

        await rig.Party.AnswerInviteAsync(t, rig.Handle(1), 7, a.CharacterUid, "A");
        await rig.Party.AnswerInviteAsync(t, rig.Handle(1), 0, a.CharacterUid, "A");
        Assert.Null(rig.Party.GetPlayerParty(a.SessionId));
        Assert.Empty(await rig.FramesAsync(0));
        Assert.Empty(await rig.FramesAsync(1));

        // The consumed invitation no longer blocks a fresh one (no 15717): the dialog opens again.
        await rig.Party.InviteToPartyAsync(a, t, "T");
        Assert.Single(await rig.FramesAsync(1));
        Assert.Empty(await rig.FramesAsync(0));
    }

    /// <summary>Answer 2 (the dialog timed out on the target's side) closes both sides with 15716.</summary>
    [Fact]
    public async Task Answer_TimedOut_ClosesBothSidesWith15716()
    {
        await using var rig = await PartyWireRig.StartAsync(2);
        var a = rig.AddPlayer(0, "A");
        var t = rig.AddPlayer(1, "T");
        await rig.Party.InviteToPartyAsync(a, t, "t");
        await rig.DrainAsync();

        await rig.Party.AnswerInviteAsync(t, rig.Handle(1), 2, a.CharacterUid, "A");

        Assert.Equal(new[] { PartyWireRig.Frame(ZoneS2COpcodes.OnPartyError,
            PartyStatePackets.BuildOnPartyError(rig.Handle(1), 15716, "A")) }, await rig.FramesAsync(1));
        Assert.Equal(new[] { Invite(rig, 0, 15716, "t") }, await rig.FramesAsync(0));
        Assert.Null(rig.Party.GetPlayerParty(a.SessionId));
    }

    // ---- join ------------------------------------------------------------------------------------------------

    /// <summary>
    /// The first accept founds the party: inviter leader and founder, slots 1 and 2, default loot. The inviter gets the
    /// accepted 2346 and then the 2357 create roster; the joiner only the same 2357.
    /// </summary>
    [Fact]
    public async Task Join_FirstAccept_FoundsThePartyAndSendsTheCreateRosterToBoth()
    {
        await using var rig = await PartyWireRig.StartAsync(2);
        var a = rig.AddPlayer(0, "A");
        var b = rig.AddPlayer(1, "B");
        await rig.Party.InviteToPartyAsync(a, b, "B");
        await rig.DrainAsync();

        await rig.Party.AnswerInviteAsync(b, rig.Handle(1), 0, a.CharacterUid, "A");

        var party = rig.Party.GetPlayerParty(a.SessionId)!;
        Assert.Same(party, rig.Party.GetPlayerParty(b.SessionId));
        Assert.Equal(new List<uint> { a.SessionId, b.SessionId }, party.MemberSessionIds);
        Assert.Equal(a.SessionId, party.LeaderSessionId);
        Assert.Equal(a.CharacterUid, party.FounderGid);
        Assert.Equal(1, PartyService.GetFrameSlot(party, a.SessionId));
        Assert.Equal(2, PartyService.GetFrameSlot(party, b.SessionId));
        Assert.Equal(PartyState.DefaultItemRule, party.ItemRule);
        Assert.Equal(PartyState.DefaultItemQuality, party.ItemQuality);
        Assert.Equal(new[] { Invite(rig, 0, 0, "B"), Roster(rig, party, create: true) }, await rig.FramesAsync(0));
        Assert.Equal(new[] { Roster(rig, party, create: true) }, await rig.FramesAsync(1));
    }

    /// <summary>
    /// Joining an existing party: every member except the inviter gets only the 2359 delta; the inviter gets the 2359 and
    /// then the accepted 2346 (the delta goes out before the inviter's answer); the joiner gets the 2357 create roster.
    /// A member who left earlier frees the lowest slot, and the joiner takes it.
    /// </summary>
    [Fact]
    public async Task Join_IntoAnExistingParty_SendsTheDeltaFirst_AndTakesTheLowestFreeSlot()
    {
        await using var rig = await PartyWireRig.StartAsync(4);
        var a = rig.AddPlayer(0, "A");
        var b = rig.AddPlayer(1, "B");
        var c = rig.AddPlayer(2, "C");
        var d = rig.AddPlayer(3, "D");
        var party = rig.TrackParty(0x63, [a, b, c]);
        await rig.Party.LeavePartyAsync(b.SessionId, PartyLeaveReason.Kicked);
        await rig.DrainAsync();
        await rig.Party.InviteToPartyAsync(c, d, "D");
        await rig.DrainAsync();

        await rig.Party.AnswerInviteAsync(d, rig.Handle(3), 0, c.CharacterUid, "C");

        Assert.Equal(new List<uint> { a.SessionId, c.SessionId, d.SessionId }, party.MemberSessionIds);
        Assert.Equal(2, PartyService.GetFrameSlot(party, d.SessionId));
        var delta = PartyWireRig.Frame(ZoneS2COpcodes.OnPartyNotifyJoin, rig.Party.BuildJoinDeltaBody(party, d));
        Assert.Equal(new[] { delta }, await rig.FramesAsync(0));
        Assert.Equal(new[] { delta, Invite(rig, 2, 0, "D") }, await rig.FramesAsync(2));
        Assert.Equal(new[] { Roster(rig, party, create: true) }, await rig.FramesAsync(3));
        Assert.Empty(await rig.FramesAsync(1));
    }

    /// <summary>
    /// A character the party holds as an offline member takes that member back on accept even when the party is at its
    /// cap: position, slot and leadership stay, the lock clears, the count does not grow.
    /// </summary>
    [Fact]
    public async Task Join_OfflineMemberByGid_ReplacesTheSessionInPlace_EvenAtTheCap()
    {
        await using var rig = await PartyWireRig.StartAsync(5);
        var a = rig.AddPlayer(0, "A");
        var b = rig.AddPlayer(1, "B");
        var c = rig.AddPlayer(2, "C");
        var gone = rig.AddPlayer(3, "Gone");
        var party = rig.TrackParty(0x64, [a, gone, b, c]);
        party.LeaderSessionId = gone.SessionId;
        rig.Players.RemovePlayer(gone.SessionId, out _);
        await rig.Party.NotifyMemberOfflineAsync(gone);
        Assert.True(party.LockedSince.ContainsKey(gone.CharacterUid));

        // The same character on the fifth session, invited while the party still has room.
        var back = new PlayerState
        {
            SessionId = rig.Sessions[4].SessionId, EntityId = rig.Handle(4), CharacterId = gone.CharacterId,
            CharacterUid = gone.CharacterUid, Name = "Gone", Level = 50, MapId = 1100,
        };
        rig.Players.AddPlayer(back.SessionId, back);
        await rig.Party.InviteToPartyAsync(a, back, "Gone");
        // A placeholder member fills the party to the cap of 5 before the answer.
        party.MemberSessionIds.Add(9999);
        party.FrameSlots[9999] = 5;
        await rig.DrainAsync();

        await rig.Party.AnswerInviteAsync(back, rig.Handle(4), 0, a.CharacterUid, "A");

        Assert.Equal(new List<uint> { a.SessionId, back.SessionId, b.SessionId, c.SessionId, 9999 }, party.MemberSessionIds);
        Assert.Equal(back.SessionId, party.LeaderSessionId);
        Assert.Equal(2, PartyService.GetFrameSlot(party, back.SessionId));
        Assert.False(party.LockedSince.ContainsKey(gone.CharacterUid));
        Assert.Same(party, rig.Party.GetPlayerParty(back.SessionId));
        Assert.Null(rig.Party.GetPlayerParty(gone.SessionId));
        Assert.Equal(new[] { Roster(rig, party, create: true) }, await rig.FramesAsync(4));
    }

    // ---- leave, disband, succession --------------------------------------------------------------------------

    /// <summary>
    /// A member that leaves by its own request: the remaining members (not the leaver, already removed from the roster)
    /// get 2360 kind 0 and then the 2361 roster. A kicked member sends no 2360, only the roster.
    /// </summary>
    [Fact]
    public async Task Leave_Left_Sends2360ThenRosterToTheRemaining_AndKickedSendsOnlyTheRoster()
    {
        await using var rig = await PartyWireRig.StartAsync(4);
        var a = rig.AddPlayer(0, "A");
        var b = rig.AddPlayer(1, "B");
        var c = rig.AddPlayer(2, "C");
        var d = rig.AddPlayer(3, "D");
        var party = rig.TrackParty(0x65, [a, b, c, d]);
        await rig.DrainAsync();

        await rig.Party.LeavePartyAsync(b.SessionId, PartyLeaveReason.Left);

        Assert.Empty(await rig.FramesAsync(1));
        Assert.Null(rig.Party.GetPlayerParty(b.SessionId));
        Assert.Equal(a.SessionId, party.LeaderSessionId);
        var left = new[] { Out2360(party, b), Roster(rig, party) };
        Assert.Equal(left, await rig.FramesAsync(0));
        Assert.Equal(left, await rig.FramesAsync(2));
        Assert.Equal(left, await rig.FramesAsync(3));

        await rig.Party.LeavePartyAsync(c.SessionId, PartyLeaveReason.Kicked);

        Assert.Empty(await rig.FramesAsync(2));
        Assert.Equal(new[] { Roster(rig, party) }, await rig.FramesAsync(0));
        Assert.Equal(new[] { Roster(rig, party) }, await rig.FramesAsync(3));
    }

    /// <summary>
    /// The leader leaves: the successor is the first member in roster order who is in the world (an offline first member
    /// is skipped), and the order to the others is 2360, 2365 (new leader), 2361.
    /// </summary>
    [Fact]
    public async Task Leave_OfTheLeader_PromotesTheFirstOnlineMember_2360Then2365Then2361()
    {
        await using var rig = await PartyWireRig.StartAsync(4);
        var a = rig.AddPlayer(0, "A");
        var away = rig.AddPlayer(1, "Away");
        var c = rig.AddPlayer(2, "C");
        var d = rig.AddPlayer(3, "D");
        var party = rig.TrackParty(0x66, [a, away, c, d]);
        rig.Players.RemovePlayer(away.SessionId, out _);
        await rig.Party.NotifyMemberOfflineAsync(away);
        await rig.DrainAsync();

        await rig.Party.LeavePartyAsync(a.SessionId, PartyLeaveReason.Left);

        Assert.Equal(c.SessionId, party.LeaderSessionId);
        Assert.Empty(await rig.FramesAsync(0));
        Assert.Empty(await rig.FramesAsync(1));
        var expected = new[] { Out2360(party, a), Master2365(party, c), Roster(rig, party) };
        Assert.Equal(expected, await rig.FramesAsync(2));
        Assert.Equal(expected, await rig.FramesAsync(3));
    }

    /// <summary>
    /// A leave that leaves one member ends the party. The service-level kick leaves one 2358 (the kick handler already sent
    /// the first copy) and no 2360; the removed member is told nothing here.
    /// </summary>
    [Fact]
    public async Task Leave_Kicked_ThatDisbands_SendsOneDestroy_AndNoOutNotice()
    {
        await using var rig = await PartyWireRig.StartAsync(2);
        var a = rig.AddPlayer(0, "A");
        var b = rig.AddPlayer(1, "B");
        var party = rig.TrackParty(0x67, [a, b]);
        await rig.DrainAsync();

        await rig.Party.LeavePartyAsync(b.SessionId, PartyLeaveReason.Kicked);

        Assert.Equal(new[] { Destroy2358(party) }, await rig.FramesAsync(0));
        Assert.Empty(await rig.FramesAsync(1));
        Assert.Null(rig.Party.GetPlayerParty(a.SessionId));
    }

    /// <summary>
    /// The last other member is offline: leaving ends the party, nobody receives a 2358, and the offline member's key is
    /// released too.
    /// </summary>
    [Fact]
    public async Task Leave_ThatDisbands_SkipsAnOfflineSurvivor_ButReleasesItsKey()
    {
        await using var rig = await PartyWireRig.StartAsync(2);
        var a = rig.AddPlayer(0, "A");
        var away = rig.AddPlayer(1, "Away");
        var party = rig.TrackParty(0x68, [a, away]);
        rig.Players.RemovePlayer(away.SessionId, out _);
        await rig.Party.NotifyMemberOfflineAsync(away);
        await rig.DrainAsync();

        await rig.Party.LeavePartyAsync(a.SessionId, PartyLeaveReason.Left);

        Assert.Empty(await rig.FramesAsync(0));
        Assert.Empty(await rig.FramesAsync(1));
        Assert.Null(rig.Party.GetPlayerParty(a.SessionId));
        Assert.Null(rig.Party.GetPlayerParty(away.SessionId));
    }

    // ---- sub-master ------------------------------------------------------------------------------------------

    /// <summary>
    /// The leader's promotion answers 2702 and then notifies 2703 to every member, the leader included, who therefore gets
    /// both; the dismissal is 2706 then 2707 the same way. A slot holding a GID that is no longer a member is cleared
    /// when the next promotion looks for a free slot; a freed slot is the next one used.
    /// </summary>
    [Fact]
    public async Task SubMaster_PromoteAndDismiss_AnswerThenNotifyEveryone_AndSlotsAreReused()
    {
        await using var rig = await PartyWireRig.StartAsync(3);
        var a = rig.AddPlayer(0, "A");
        var b = rig.AddPlayer(1, "B");
        var c = rig.AddPlayer(2, "C");
        var party = rig.TrackParty(0x69, [a, b, c]);
        party.SubMasterGids[0] = 0x0DE0B6B3A7FFFFFFUL; // a GID that is no member
        await rig.DrainAsync();

        Assert.True(await rig.Party.SetSubMasterAsync(a.SessionId, b.CharacterUid));

        Assert.Equal(b.CharacterUid, party.SubMasterGids[0]);
        var nfy = PartyWireRig.Frame(ZoneS2COpcodes.NfyPartySubMaster, PartyStatePackets.BuildNfyPartySubMaster(b.CharacterUid));
        Assert.Equal(new[] { PartyWireRig.Frame(ZoneS2COpcodes.AnsPartySubMaster, PartyStatePackets.BuildAnsPartySubMaster2702()), nfy },
            await rig.FramesAsync(0));
        Assert.Equal(new[] { nfy }, await rig.FramesAsync(1));
        Assert.Equal(new[] { nfy }, await rig.FramesAsync(2));

        Assert.True(await rig.Party.SetSubMasterAsync(a.SessionId, c.CharacterUid));
        Assert.Equal(new[] { b.CharacterUid, c.CharacterUid, 0UL }, party.SubMasterGids);
        await rig.DrainAsync();

        Assert.True(await rig.Party.DismissSubMasterAsync(a.SessionId, b.CharacterUid));

        Assert.Equal(new[] { 0UL, c.CharacterUid, 0UL }, party.SubMasterGids);
        var dismissed = PartyWireRig.Frame(ZoneS2COpcodes.NfyPartyDismissSubmaster,
            PartyStatePackets.BuildNfyPartyDismissSubmaster(b.CharacterUid));
        Assert.Equal(new[] { PartyWireRig.Frame(ZoneS2COpcodes.AnsPartyDismissSubMaster,
            PartyStatePackets.BuildAnsPartyDismissSubMaster2706()), dismissed }, await rig.FramesAsync(0));
        Assert.Equal(new[] { dismissed }, await rig.FramesAsync(1));
        Assert.Equal(new[] { dismissed }, await rig.FramesAsync(2));

        Assert.True(await rig.Party.SetSubMasterAsync(a.SessionId, b.CharacterUid));
        Assert.Equal(new[] { b.CharacterUid, c.CharacterUid, 0UL }, party.SubMasterGids);
    }

    // ---- frame move ------------------------------------------------------------------------------------------

    private static PartyFrameMoveHandler FrameMoveHandler(PartyWireRig rig)
    {
        var provider = new ServiceCollection().AddSingleton(rig.Party).BuildServiceProvider();
        return new PartyFrameMoveHandler(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<PartyFrameMoveHandler>.Instance, rig.Players, rig.Broadcast);
    }

    private static byte[] Move1504(ulong moved, ulong occupant, byte slot)
    {
        var body = new byte[17];
        BinaryPrimitives.WriteUInt64LittleEndian(body, moved);
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(8), occupant);
        body[16] = slot;
        return body;
    }

    private static string Move2705(ulong gid1, byte slot1, ulong gid2, byte slot2)
        => PartyWireRig.Frame(ZoneS2COpcodes.NfyPartyFrameMove, PartyAnswerPackets.BuildNfyPartyFrameMove2705(gid1, slot1, gid2, slot2));

    /// <summary>
    /// Invalid frame moves change nothing and send nothing: a short body, a zero moved GID, slot 0, a slot above the type's
    /// cap (5 for a normal party), a GID that is no member, and an occupant GID that is no member.
    /// </summary>
    [Fact]
    public async Task FrameMove_Invalid_ChangesNothingAndSendsNothing()
    {
        await using var rig = await PartyWireRig.StartAsync(2);
        var a = rig.AddPlayer(0, "A");
        var b = rig.AddPlayer(1, "B");
        var party = rig.TrackParty(0x6A, [a, b]);
        await rig.DrainAsync();
        var handler = FrameMoveHandler(rig);
        var stranger = 0x0DE0B6B3A7FFFFFFUL;

        foreach (var body in new[]
        {
            Move1504(b.CharacterUid, 0, 3)[..16], Move1504(0, 0, 3), Move1504(b.CharacterUid, 0, 0),
            Move1504(b.CharacterUid, 0, 6), Move1504(stranger, 0, 3), Move1504(b.CharacterUid, stranger, 3),
        })
            await handler.HandleAsync(rig.Sessions[0], body);

        Assert.Empty(await rig.FramesAsync(0));
        Assert.Empty(await rig.FramesAsync(1));
        Assert.Equal(1, PartyService.GetFrameSlot(party, a.SessionId));
        Assert.Equal(2, PartyService.GetFrameSlot(party, b.SessionId));
    }

    /// <summary>
    /// A move onto an occupied slot swaps the two members' slots: the sender gets the empty 1504 answer and then the 2705
    /// naming both; the other members only the 2705. A move onto an empty slot sends 2705 with a zero second GID and slot.
    /// When the client sees the slot empty but the server holds a member there, the move still swaps.
    /// </summary>
    [Fact]
    public async Task FrameMove_SwapsOccupied_MovesToEmpty_AndSwapsWhenTheClientSawItEmpty()
    {
        await using var rig = await PartyWireRig.StartAsync(3);
        var a = rig.AddPlayer(0, "A");
        var b = rig.AddPlayer(1, "B");
        var c = rig.AddPlayer(2, "C");
        var party = rig.TrackParty(0x6B, [a, b, c]);
        await rig.DrainAsync();
        var handler = FrameMoveHandler(rig);
        var answer = PartyWireRig.Frame(ZoneS2COpcodes.AnsPartyFrameMove, new byte[] { 0, 0 });

        // C (slot 3) takes B's slot 2: slots swap.
        await handler.HandleAsync(rig.Sessions[0], Move1504(c.CharacterUid, b.CharacterUid, 2));
        var swap = Move2705(c.CharacterUid, 2, b.CharacterUid, 3);
        Assert.Equal(new[] { answer, swap }, await rig.FramesAsync(0));
        Assert.Equal(new[] { swap }, await rig.FramesAsync(1));
        Assert.Equal(new[] { swap }, await rig.FramesAsync(2));
        Assert.Equal(2, PartyService.GetFrameSlot(party, c.SessionId));
        Assert.Equal(3, PartyService.GetFrameSlot(party, b.SessionId));

        // A moves to the empty slot 5.
        await handler.HandleAsync(rig.Sessions[0], Move1504(a.CharacterUid, 0, 5));
        var empty = Move2705(a.CharacterUid, 5, 0, 0);
        Assert.Equal(new[] { answer, empty }, await rig.FramesAsync(0));
        Assert.Equal(new[] { empty }, await rig.FramesAsync(1));
        Assert.Equal(5, PartyService.GetFrameSlot(party, a.SessionId));

        // B (slot 3) is sent to slot 2 as "empty"; the server has C there, so they swap.
        await handler.HandleAsync(rig.Sessions[1], Move1504(b.CharacterUid, 0, 2));
        var forced = Move2705(b.CharacterUid, 2, c.CharacterUid, 3);
        Assert.Equal(new[] { forced }, await rig.FramesAsync(0));
        Assert.Equal(new[] { answer, forced }, await rig.FramesAsync(1));
        Assert.Equal(2, PartyService.GetFrameSlot(party, b.SessionId));
        Assert.Equal(3, PartyService.GetFrameSlot(party, c.SessionId));
    }

    // ---- helper ----------------------------------------------------------------------------------------------

    /// <summary>
    /// A valid declaration answers the declarer with 2902 and then every member gets the 2361 roster carrying the flag;
    /// withdrawing answers 2902 with flag 0. A declarer who does not meet the rule (no member 10 or more levels lower)
    /// gets nothing and no flag is stored.
    /// </summary>
    [Fact]
    public async Task Helper_Declare_Answers2902ThenBroadcastsTheRoster_AndAnIneligibleOneSendsNothing()
    {
        await using var rig = await PartyWireRig.StartAsync(2);
        var high = rig.AddPlayer(0, "High");
        var low = rig.AddPlayer(1, "Low");
        low.Level = 30;
        var party = rig.TrackParty(0x6C, [high, low]);
        await rig.DrainAsync();

        Assert.Equal(HelperDeclareResult.NotEligible, await rig.Party.SetHelperFlagAsync(low, HelperPackets.FlagDeclared));
        Assert.Empty(await rig.FramesAsync(0));
        Assert.Empty(await rig.FramesAsync(1));
        Assert.True(party.HelperFlags.IsEmpty);

        Assert.Equal(HelperDeclareResult.Done, await rig.Party.SetHelperFlagAsync(high, HelperPackets.FlagDeclared));
        Assert.Equal(HelperPackets.FlagDeclared, party.HelperFlags[high.CharacterUid]);
        Assert.Equal(new[]
        {
            PartyWireRig.Frame(ZoneS2COpcodes.AnsHelperDeclare, HelperPackets.BuildAnsHelperDeclare2902(high.CharacterUid, 1, 0)),
            Roster(rig, party),
        }, await rig.FramesAsync(0));
        Assert.Equal(new[] { Roster(rig, party) }, await rig.FramesAsync(1));

        Assert.Equal(HelperDeclareResult.Done, await rig.Party.SetHelperFlagAsync(high, HelperPackets.FlagQuit));
        Assert.True(party.HelperFlags.IsEmpty);
        Assert.Equal(new[]
        {
            PartyWireRig.Frame(ZoneS2COpcodes.AnsHelperDeclare, HelperPackets.BuildAnsHelperDeclare2902(high.CharacterUid, 0, 0)),
            Roster(rig, party),
        }, await rig.FramesAsync(0));
    }
}
