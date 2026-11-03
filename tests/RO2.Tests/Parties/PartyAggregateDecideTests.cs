using RO2.Parties.Domain;
using Xunit;

namespace RO2.Tests.Parties;

/// <summary>
/// The pure PartyAggregate decision chains, asserted as (outcome, ordered effects). They mirror what
/// PartyDecideChainsTests and the other party tests pin at the host (same decisions, same order, same recipients);
/// the host byte builders stay the wire oracle, so effects are asserted here, not bytes.
/// </summary>
public class PartyAggregateDecideTests
{
    private static readonly DateTime Now = new(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);

    private static ulong G(int n) => 0x0DE0B6B3A7000000UL + (ulong)n;

    private static HashSet<ulong> Online(params int[] members) => [.. members.Select(G)];

    private static PartyMessage M(PartyMessageKind kind, ulong gid = 0, int code = 0, string text = "", byte value = 0,
        ulong gid2 = 0, byte value2 = 0) => new(kind, gid, code, text, value, gid2, value2);

    /// <summary>A party of members G(1)..G(count) in roster order, G(1) leader and founder, built through DecideJoin.</summary>
    private static PartyAggregate Party(int count, uint id = 0x61)
    {
        PartyAggregate? party = null;
        for (var i = 2; i <= count; i++)
        {
            var (outcome, _) = PartyAggregate.DecideJoin(party, id, G(1), "M1", G(i), "M" + i, false, 0, Online(), Now);
            party = outcome.Party;
        }
        return party!;
    }

    private static ulong[] Roster(PartyAggregate p) => [.. p.Members];

    // ---- invite ----------------------------------------------------------------------------------------------

    [Fact]
    public void Invite_Self_IsIgnored()
    {
        var (o, fx) = PartyAggregate.DecideInvite(G(1), "A", G(1), "A", null, false, true, null, Now);
        Assert.Equal(PartyResult.Ignored, o.Result);
        Assert.Empty(fx);
    }

    [Fact]
    public void Invite_TargetInAParty_Is15710_ToTheInviterOnly()
    {
        var (o, fx) = PartyAggregate.DecideInvite(G(1), "A", G(3), "c", null, targetInParty: true, true, null, Now);
        Assert.Equal(new PartyOutcome(PartyResult.Refused, 15710), o);
        Assert.Equal<PartyEffect>([new SendToMember(G(1), M(PartyMessageKind.InviteResult, code: 15710, text: "c"))], fx);
    }

    [Fact]
    public void Invite_FullPartyIsRefused15707_AndARaidOfFiveIsNot()
    {
        var party = Party(5);
        var (o, fx) = PartyAggregate.DecideInvite(G(1), "A", G(9), "x", party, false, true, null, Now);
        Assert.Equal(15707, o.Code);
        Assert.Equal<PartyEffect>([new SendToMember(G(1), M(PartyMessageKind.InviteResult, code: 15707, text: "x"))], fx);

        party.PartyType = PartyAggregate.PartyTypeRaid;
        Assert.Equal(PartyResult.InviteOpened, PartyAggregate.DecideInvite(G(1), "A", G(9), "x", party, false, true, null, Now).Item1.Result);
    }

    [Fact]
    public void Invite_UnreachableTarget_IsIgnored()
    {
        var (o, fx) = PartyAggregate.DecideInvite(G(1), "A", G(2), "B", null, false, targetReachable: false, null, Now);
        Assert.Equal(PartyResult.Ignored, o.Result);
        Assert.Empty(fx);
    }

    [Fact]
    public void Invite_WhileOneIsOpen_Is15717_ButAnExpiredOneIsReplaced()
    {
        var open = new PartyInvite(G(5), "T", Now, 0);
        var (o, fx) = PartyAggregate.DecideInvite(G(1), "A", G(2), "t", null, false, true, open, Now.AddSeconds(29));
        Assert.Equal(new PartyOutcome(PartyResult.Refused, 15717), o);
        Assert.Equal<PartyEffect>([new SendToMember(G(1), M(PartyMessageKind.InviteResult, code: 15717, text: "t"))], fx);

        (o, fx) = PartyAggregate.DecideInvite(G(1), "A", G(2), "t", null, false, true, open, Now.AddSeconds(30));
        Assert.Equal(PartyResult.InviteOpened, o.Result);
        Assert.Equal(new PartyInvite(G(1), "t", Now.AddSeconds(30), 0), o.Invite);
        Assert.Equal<PartyEffect>([new SendToMember(G(2), M(PartyMessageKind.InviteDialog, gid: G(1), text: "A"))], fx);
    }

    [Fact]
    public void Invite_CarriesTheInviterPartyIdAndType()
    {
        var party = Party(2, id: 0x99);
        party.PartyType = PartyAggregate.PartyTypeRaid;
        var (o, fx) = PartyAggregate.DecideInvite(G(1), "A", G(9), "x", party, false, true, null, Now);
        Assert.Equal(0x99u, o.Invite!.InviterPartyId);
        Assert.Equal<PartyEffect>([new SendToMember(G(9), M(PartyMessageKind.InviteDialog, gid: G(1), text: "A", value: 2))], fx);
    }

    // ---- answer ----------------------------------------------------------------------------------------------

    private static readonly PartyInvite Pending = new(G(1), "T", Now, 0);

    [Fact]
    public void Answer_NoInviteOrWrongInviterGid_IsIgnored_AndLeavesTheInvitation()
    {
        Assert.Equal(PartyResult.Ignored, PartyAggregate.DecideAnswer(null, G(2), G(1), 0, G(1), "A", 0).Item1.Result);
        var (o, fx) = PartyAggregate.DecideAnswer(Pending, G(2), G(1), 0, G(1) + 1, "A", 0);
        Assert.Equal(PartyResult.Ignored, o.Result);
        Assert.Empty(fx);
    }

    [Fact]
    public void Answer_UnknownCode_ConsumesTheInvitationWithoutAFrame()
    {
        var (o, fx) = PartyAggregate.DecideAnswer(Pending, G(2), G(1), 7, G(1), "A", 0);
        Assert.Equal(PartyResult.InviteConsumed, o.Result);
        Assert.Empty(fx);
    }

    [Fact]
    public void Answer_Accept_HandsOverToJoin()
    {
        var (o, fx) = PartyAggregate.DecideAnswer(Pending, G(2), G(1), PartyAggregate.AnswerAccept, G(1), "A", 0);
        Assert.Equal(PartyResult.JoinNext, o.Result);
        Assert.Empty(fx);
    }

    [Theory]
    [InlineData((ushort)1, 15715)]
    [InlineData((ushort)2, 15716)]
    public void Answer_DeclineOrTimeout_ClosesBothSides(ushort answer, int code)
    {
        var (o, fx) = PartyAggregate.DecideAnswer(Pending, G(2), G(1), answer, G(1), "A", 2);
        Assert.Equal(new PartyOutcome(PartyResult.InviteConsumed, code), o);
        Assert.Equal<PartyEffect>(
        [
            new SendToMember(G(2), M(PartyMessageKind.Error, code: code, text: "A")),
            new SendToMember(G(1), M(PartyMessageKind.InviteResult, code: code, text: "T", value: 2)),
        ], fx);
    }

    [Fact]
    public void Answer_InviterGone_ConsumesIt_AndOnlyAnAcceptGets15708()
    {
        var (o, fx) = PartyAggregate.DecideAnswer(Pending, G(2), null, PartyAggregate.AnswerAccept, G(1), "A", 0);
        Assert.Equal(PartyResult.InviteConsumed, o.Result);
        Assert.Equal<PartyEffect>([new SendToMember(G(2), M(PartyMessageKind.Error, code: 15708, text: "A"))], fx);

        (o, fx) = PartyAggregate.DecideAnswer(Pending, G(2), null, PartyAggregate.AnswerDecline, G(1), "A", 0);
        Assert.Equal(PartyResult.InviteConsumed, o.Result);
        Assert.Empty(fx);
    }

    // ---- join ------------------------------------------------------------------------------------------------

    [Fact]
    public void Join_FirstAccept_FoundsThePartyAndSendsTheCreateRosterToBoth()
    {
        var (o, fx) = PartyAggregate.DecideJoin(null, 7, G(1), "A", G(2), "B", false, 0, Online(1, 2), Now);

        Assert.Equal(PartyResult.Created, o.Result);
        var p = o.Party!;
        Assert.Equal(7u, p.Id);
        Assert.Equal([G(1), G(2)], Roster(p));
        Assert.Equal(G(1), p.LeaderGid);
        Assert.Equal(G(1), p.FounderGid);
        Assert.Equal(Now, p.CreatedAt);
        Assert.Equal(1, p.FrameSlotOf(G(1)));
        Assert.Equal(2, p.FrameSlotOf(G(2)));
        Assert.Equal(PartyAggregate.DefaultItemRule, p.ItemRule);
        Assert.Equal(PartyAggregate.DefaultItemQuality, p.ItemQuality);
        Assert.Equal<PartyEffect>(
        [
            new SendToMember(G(1), M(PartyMessageKind.InviteResult, text: "B")),
            new SendRosterCreate(null),
        ], fx);
    }

    [Fact]
    public void Join_IntoAnExistingParty_SendsTheDeltaFirst_ThenTheInviterAnswer_ThenTheJoinersRoster()
    {
        var party = Party(3);
        var (o, fx) = PartyAggregate.DecideJoin(party, 0, G(3), "C", G(4), "D", false, party.Id, Online(1, 2, 3), Now);

        Assert.Equal(PartyResult.Done, o.Result);
        Assert.Same(party, o.Party);
        Assert.Equal<PartyEffect>(
        [
            new SendJoinDelta(G(4)),
            new SendToMember(G(3), M(PartyMessageKind.InviteResult, text: "D")),
            new SendRosterCreate(G(4)),
        ], fx);
    }

    [Fact]
    public void Join_TakesTheLowestFreeSlot_AfterAMemberLeft()
    {
        var party = Party(3);
        party.DecideLeave(G(2), PartyLeaveReason.Kicked, Online(1, 3));

        var (o, _) = PartyAggregate.DecideJoin(party, 0, G(3), "C", G(4), "D", false, party.Id, Online(1, 3), Now);

        Assert.Equal([G(1), G(3), G(4)], Roster(party));
        Assert.Equal(2, party.FrameSlotOf(G(4)));
        Assert.Equal(PartyResult.Done, o.Result);
    }

    [Fact]
    public void Join_OfflineMemberByGid_IsTakenBackInPlace_EvenAtTheCap_AndClearsTheLock()
    {
        var party = Party(5);
        party.DecideMemberLocked(G(2), Now);

        var (o, fx) = PartyAggregate.DecideJoin(party, 0, G(1), "A", G(2), "B", false, party.Id, Online(1, 3, 4, 5), Now);

        Assert.Equal(PartyResult.Done, o.Result);
        Assert.Equal([G(1), G(2), G(3), G(4), G(5)], Roster(party));
        Assert.Equal(2, party.FrameSlotOf(G(2)));
        Assert.Empty(party.LockedSince);
        Assert.Equal<PartyEffect>(
        [
            new SendJoinDelta(G(2)),
            new SendToMember(G(1), M(PartyMessageKind.InviteResult, text: "B")),
            new SendRosterCreate(G(2)),
        ], fx);
    }

    [Fact]
    public void Join_StaleRecordOfALeader_KeepsLeadership()
    {
        var party = Party(3);
        party.DecideChangeLeader(G(1), G(2), Online(1, 2, 3), false);

        PartyAggregate.DecideJoin(party, 0, G(1), "A", G(2), "B", false, party.Id, Online(1, 3), Now);

        Assert.Equal(G(2), party.LeaderGid);
    }

    [Fact]
    public void Join_RefusalsSendTheErrorToTheJoiner_AndChangeNothing()
    {
        var full = Party(5);
        var (o, fx) = PartyAggregate.DecideJoin(full, 0, G(1), "A", G(9), "Z", false, full.Id, Online(1, 2, 3, 4, 5), Now);
        Assert.Equal(new PartyOutcome(PartyResult.Refused, 15707), o);
        Assert.Equal<PartyEffect>([new SendToMember(G(9), M(PartyMessageKind.Error, code: 15707, text: "A"))], fx);
        Assert.Equal(5, full.Members.Count);

        (o, fx) = PartyAggregate.DecideJoin(null, 3, G(1), "A", G(2), "B", joinerInParty: true, 0, Online(), Now);
        Assert.Equal(new PartyOutcome(PartyResult.Refused, 15710), o);
        Assert.Null(o.Party);

        (o, _) = PartyAggregate.DecideJoin(full, 0, G(1), "A", G(9), "Z", false, full.Id + 1, Online(), Now);
        Assert.Equal(15708, o.Code);
        (o, _) = PartyAggregate.DecideJoin(null, 3, G(1), "A", G(9), "Z", false, 0x77, Online(), Now);
        Assert.Equal(15708, o.Code);
    }

    // ---- leave, disband, succession --------------------------------------------------------------------------

    [Fact]
    public void Leave_Left_Sends2360ToTheRemainingThenTheRoster_NeverToTheLeaver()
    {
        var party = Party(4);

        var (o, fx) = party.DecideLeave(G(2), PartyLeaveReason.Left, Online(1, 3, 4));

        Assert.Equal(PartyResult.Done, o.Result);
        Assert.DoesNotContain(G(2), party.Members);
        Assert.Equal(G(1), party.LeaderGid);
        Assert.Equal<PartyEffect>(
        [
            new SendToParty(M(PartyMessageKind.Out, gid: G(2), value: PartyAggregate.OutKindRemoved)),
            new SendRosterUpdate(),
        ], fx);
    }

    [Fact]
    public void Leave_Kicked_SendsOnlyTheRoster()
    {
        var party = Party(4);
        var (_, fx) = party.DecideLeave(G(3), PartyLeaveReason.Kicked, Online(1, 2, 4));
        Assert.Equal<PartyEffect>([new SendRosterUpdate()], fx);
    }

    [Fact]
    public void Leave_OfTheLeader_PromotesTheFirstOnlineMember_Out_ThenLeader_ThenRoster()
    {
        var party = Party(4);

        var (_, fx) = party.DecideLeave(G(1), PartyLeaveReason.Left, Online(3, 4)); // member 2 is offline

        Assert.Equal(G(3), party.LeaderGid);
        Assert.Equal<PartyEffect>(
        [
            new SendToParty(M(PartyMessageKind.Out, gid: G(1), value: PartyAggregate.OutKindRemoved)),
            new SendToParty(M(PartyMessageKind.LeaderChange, gid: G(3))),
            new SendRosterUpdate(),
        ], fx);
    }

    [Fact]
    public void Leave_OfTheLeader_WithNoOneOnline_FallsBackToTheFirstMember()
    {
        var party = Party(3);
        party.DecideLeave(G(1), PartyLeaveReason.Left, Online());
        Assert.Equal(G(2), party.LeaderGid);
    }

    [Fact]
    public void Leave_Kicked_ThatDisbands_SendsOneDestroyAndNoOutNotice()
    {
        var party = Party(2);

        var (o, fx) = party.DecideLeave(G(2), PartyLeaveReason.Kicked, Online(1));

        Assert.Equal(PartyResult.Disbanded, o.Result);
        Assert.True(party.IsDisbanded);
        Assert.Equal<PartyEffect>([new SendDestroy(1, [G(1)])], fx);
    }

    [Fact]
    public void Leave_Left_ThatDisbands_SendsTwoDestroys_AfterTheOutNotice_SkippingAnOfflineSurvivor()
    {
        var party = Party(2);
        var (_, fx) = party.DecideLeave(G(1), PartyLeaveReason.Left, Online());
        Assert.Equal<PartyEffect>(
        [
            new SendToParty(M(PartyMessageKind.Out, gid: G(1), value: PartyAggregate.OutKindRemoved)),
            new SendDestroy(2, []),
        ], fx);

        var online = Party(2);
        var (_, fx2) = online.DecideLeave(G(1), PartyLeaveReason.Left, Online(2));
        Assert.Equal(new SendDestroy(2, [G(2)]), fx2[1]);
    }

    [Fact]
    public void Leave_ClearsTheLeaversHelperFlagAndSubMasterSlot_AndIgnoresANonMember()
    {
        var party = Party(4);
        party.DecideSetSubMaster(G(1), G(3));
        party.DecideSetHelperFlag(G(1), 1, new Dictionary<ulong, byte> { [G(1)] = 50, [G(2)] = 30, [G(3)] = 30, [G(4)] = 30 }, Online(1));

        party.DecideLeave(G(3), PartyLeaveReason.Left, Online(1, 2, 4));
        party.DecideLeave(G(1), PartyLeaveReason.Left, Online(2, 4));

        Assert.Equal([0UL, 0UL, 0UL], party.SubMasters);
        Assert.Empty(party.HelperFlags);
        var (o, fx) = party.DecideLeave(G(9), PartyLeaveReason.Left, Online());
        Assert.Equal(PartyResult.NotMember, o.Result);
        Assert.Empty(fx);
    }

    [Fact]
    public void Disband_MarksThePartyAndAddressesTheOnlineMembers()
    {
        var party = Party(3);
        var (o, fx) = party.DecideDisband(2, Online(1, 3));
        Assert.Equal(PartyResult.Disbanded, o.Result);
        Assert.True(party.IsDisbanded);
        Assert.Equal<PartyEffect>([new SendDestroy(2, [G(1), G(3)])], fx);
    }

    // ---- kick ------------------------------------------------------------------------------------------------

    [Fact]
    public void Kick_OfANonMember_Is15712_AndChangesNothing()
    {
        var party = Party(3);
        var (o, fx) = party.DecideKick(G(1), G(9), Online(1, 2, 3));
        Assert.Equal(new PartyOutcome(PartyResult.Refused, 15712), o);
        Assert.Equal<PartyEffect>([new SendToMember(G(1), M(PartyMessageKind.ExpulsionAck, code: 15712))], fx);
        Assert.Equal(3, party.Members.Count);
    }

    [Fact]
    public void Kick_ThatDisbands_SendsDestroyAckNotifyThenLayoff_AndOneMoreDestroy()
    {
        var party = Party(2);

        var (o, fx) = party.DecideKick(G(1), G(2), Online(1, 2));

        Assert.Equal(PartyResult.Disbanded, o.Result);
        Assert.Equal<PartyEffect>(
        [
            new SendToMember(G(1), M(PartyMessageKind.Destroy)),
            new SendToMember(G(1), M(PartyMessageKind.ExpulsionAck)),
            new SendToPartyExcept(G(2), M(PartyMessageKind.ExpulsionNotify, gid: G(2))),
            new SendLayoff(G(2)),
            new SendDestroy(1, [G(1)]),
        ], fx);
    }

    [Fact]
    public void Kick_InABiggerParty_HasNoDestroy_NoOutNotice_AndSkipsTheLayoffOfAnOfflineTarget()
    {
        var party = Party(3);

        var (o, fx) = party.DecideKick(G(1), G(2), Online(1, 3));

        Assert.Equal(PartyResult.Done, o.Result);
        Assert.Equal([G(1), G(3)], Roster(party));
        Assert.Equal<PartyEffect>(
        [
            new SendToMember(G(1), M(PartyMessageKind.ExpulsionAck)),
            new SendToPartyExcept(G(2), M(PartyMessageKind.ExpulsionNotify, gid: G(2))),
            new SendRosterUpdate(),
        ], fx);
    }

    [Fact]
    public void Kick_ByANonLeader_ZeroGid_OrSelf_IsDropped()
    {
        var party = Party(3);
        Assert.Equal(PartyResult.NotLeader, party.DecideKick(G(2), G(3), Online(1, 2, 3)).Item1.Result);
        Assert.Equal(PartyResult.Ignored, party.DecideKick(G(1), 0, Online(1, 2, 3)).Item1.Result);
        var (o, fx) = party.DecideKick(G(1), G(1), Online(1, 2, 3));
        Assert.Equal(PartyResult.Ignored, o.Result);
        Assert.Empty(fx);
        Assert.Equal(3, party.Members.Count);
    }

    // ---- succession and lock expiry --------------------------------------------------------------------------

    [Fact]
    public void SucceedLeader_PicksTheFirstOnlineMemberOtherThanTheDeparted()
    {
        var party = Party(4);

        var (o, fx) = party.DecideSucceedLeader(G(1), Online(3, 4));

        Assert.Equal(new PartyOutcome(PartyResult.Done, Gid: G(3)), o);
        Assert.Equal(G(3), party.LeaderGid);
        Assert.Equal<PartyEffect>([new SendToParty(M(PartyMessageKind.LeaderChange, gid: G(3)))], fx);
    }

    [Fact]
    public void SucceedLeader_WithNoOnlineSuccessor_OrADepartedNonLeader_ChangesNothing()
    {
        var party = Party(3);
        Assert.Equal(PartyResult.NoSuccessor, party.DecideSucceedLeader(G(1), Online(1)).Item1.Result);
        Assert.Equal(G(1), party.LeaderGid);
        Assert.Equal(PartyResult.Ignored, party.DecideSucceedLeader(G(2), Online(3)).Item1.Result);
    }

    [Fact]
    public void ChangeLeader_AnswersTheOldLeader_ThenTellsTheParty_ThenTheRoster()
    {
        var party = Party(3);

        var (o, fx) = party.DecideChangeLeader(G(1), G(3), Online(1, 2, 3), refusedAsWoe: false);

        Assert.Equal(PartyResult.Done, o.Result);
        Assert.Equal(G(3), party.LeaderGid);
        Assert.Equal<PartyEffect>(
        [
            new SendToMember(G(1), M(PartyMessageKind.ChangeMasterAck)),
            new SendToParty(M(PartyMessageKind.LeaderChange, gid: G(3))),
            new SendRosterUpdate(),
        ], fx);
    }

    [Fact]
    public void ChangeLeader_RefusesAnOfflineSelfOrNonLeader_AndAWoeMapWith24324()
    {
        var party = Party(3);
        Assert.Equal(PartyResult.NotMember, party.DecideChangeLeader(G(1), G(3), Online(1, 2), false).Item1.Result);
        Assert.Equal(PartyResult.NotMember, party.DecideChangeLeader(G(1), G(1), Online(1, 2, 3), false).Item1.Result);
        Assert.Equal(PartyResult.NotLeader, party.DecideChangeLeader(G(2), G(3), Online(1, 2, 3), false).Item1.Result);

        var (o, fx) = party.DecideChangeLeader(G(1), G(3), Online(1, 2, 3), refusedAsWoe: true);
        Assert.Equal(new PartyOutcome(PartyResult.Refused, 24324), o);
        Assert.Equal<PartyEffect>([new SendToMember(G(1), M(PartyMessageKind.ChangeMasterAck, code: 24324))], fx);
        Assert.Equal(G(1), party.LeaderGid);
    }

    [Fact]
    public void MemberLocked_SchedulesTheSweep_ThenTellsTheOthers()
    {
        var party = Party(3);

        var (o, fx) = party.DecideMemberLocked(G(2), Now);

        Assert.Equal(PartyResult.Done, o.Result);
        Assert.Equal(Now, party.LockedSince[G(2)]);
        Assert.Equal<PartyEffect>(
        [
            new ScheduleLockSweep(),
            new SendToPartyExcept(G(2), M(PartyMessageKind.Out, gid: G(2), value: PartyAggregate.OutKindLocked)),
        ], fx);
        Assert.Equal(PartyResult.NotMember, party.DecideMemberLocked(G(9), Now).Item1.Result);
    }

    [Fact]
    public void LockExpiry_Predicate_IsAtLeastTheLifetime_AndOnlyForLockedMembers()
    {
        var party = Party(3);
        party.DecideMemberLocked(G(2), Now);
        var lifetime = PartyAggregate.LockedMemberLifetime;

        Assert.Equal(TimeSpan.FromSeconds(595), lifetime);
        Assert.False(party.IsLockExpired(G(2), Now + lifetime - TimeSpan.FromSeconds(1)));
        Assert.True(party.IsLockExpired(G(2), Now + lifetime));
        Assert.False(party.IsLockExpired(G(3), Now + lifetime * 2));
        Assert.Equal([G(2)], party.ExpiredLocks(Now + lifetime));
        Assert.Empty(party.ExpiredLocks(Now));
    }

    [Fact]
    public void DropLocked_OfTheLeader_HandsOverFirst_ThenLeaves_ThenRoster()
    {
        var party = Party(4);
        party.DecideMemberLocked(G(1), Now);
        var later = Now + PartyAggregate.LockedMemberLifetime;

        Assert.Equal(PartyResult.Ignored, party.DecideDropLocked(G(1), later - TimeSpan.FromSeconds(1), Online(3, 4)).Item1.Result);
        var (o, fx) = party.DecideDropLocked(G(1), later, Online(3, 4));

        Assert.Equal(PartyResult.Done, o.Result);
        Assert.Equal([G(2), G(3), G(4)], Roster(party));
        Assert.Equal(G(3), party.LeaderGid);
        Assert.Empty(party.LockedSince);
        Assert.Equal<PartyEffect>(
        [
            new SendToParty(M(PartyMessageKind.LeaderChange, gid: G(3))),
            new SendToParty(M(PartyMessageKind.Out, gid: G(1), value: PartyAggregate.OutKindRemoved)),
            new SendRosterUpdate(),
        ], fx);
        Assert.Equal(PartyResult.Ignored, party.DecideDropLocked(G(1), later.AddHours(1), Online(3, 4)).Item1.Result);
    }

    [Fact]
    public void DropLocked_ThatLeavesOneMember_DisbandsTheParty()
    {
        var party = Party(2);
        party.DecideMemberLocked(G(2), Now);

        var (o, fx) = party.DecideDropLocked(G(2), Now + PartyAggregate.LockedMemberLifetime, Online(1));

        Assert.Equal(PartyResult.Disbanded, o.Result);
        Assert.Equal<PartyEffect>(
        [
            new SendToParty(M(PartyMessageKind.Out, gid: G(2), value: PartyAggregate.OutKindRemoved)),
            new SendDestroy(2, [G(1)]),
        ], fx);
    }

    [Fact]
    public void DropLocked_OfAMemberWhoCameBack_OnlyConsumesTheLock()
    {
        var party = Party(3);
        party.DecideMemberLocked(G(2), Now);

        var (o, fx) = party.DecideDropLocked(G(2), Now + PartyAggregate.LockedMemberLifetime, Online(1, 2, 3));

        Assert.Equal(PartyResult.Ignored, o.Result);
        Assert.Empty(fx);
        Assert.Equal(3, party.Members.Count);
        Assert.Empty(party.LockedSince);
    }

    // ---- sub-master ------------------------------------------------------------------------------------------

    [Fact]
    public void SubMaster_PromoteAndDismiss_AnswerThenNotifyEveryone_AndSlotsAreReused()
    {
        var party = Party(3);

        var (o, fx) = party.DecideSetSubMaster(G(1), G(2));
        Assert.Equal(PartyResult.Done, o.Result);
        Assert.Equal<PartyEffect>(
        [
            new SendToMember(G(1), M(PartyMessageKind.AnsSubMaster)),
            new SendToParty(M(PartyMessageKind.NfySubMaster, gid: G(2))),
        ], fx);

        party.DecideSetSubMaster(G(1), G(3));
        Assert.Equal([G(2), G(3), 0UL], party.SubMasters);

        (o, fx) = party.DecideDismissSubMaster(G(1), G(2));
        Assert.Equal(PartyResult.Done, o.Result);
        Assert.Equal<PartyEffect>(
        [
            new SendToMember(G(1), M(PartyMessageKind.AnsDismissSubMaster)),
            new SendToParty(M(PartyMessageKind.NfyDismissSubMaster, gid: G(2))),
        ], fx);
        Assert.Equal([0UL, G(3), 0UL], party.SubMasters);

        party.DecideSetSubMaster(G(1), G(2));
        Assert.Equal([G(2), G(3), 0UL], party.SubMasters);
    }

    [Fact]
    public void SubMaster_ReusesTheSlotALeaverFreed_AndThreeIsTheCap()
    {
        var party = Party(5);
        party.DecideSetSubMaster(G(1), G(2));
        party.DecideSetSubMaster(G(1), G(3));
        party.DecideSetSubMaster(G(1), G(4));
        Assert.Equal([G(2), G(3), G(4)], party.SubMasters);

        var (o, fx) = party.DecideSetSubMaster(G(1), G(5));
        Assert.Equal(PartyResult.Refused, o.Result);
        Assert.Empty(fx);

        // A leaver's slot is cleared by Leave, so the freed slot is the next one used.
        party.DecideLeave(G(3), PartyLeaveReason.Kicked, Online(1, 2, 4, 5));
        Assert.Equal([G(2), 0UL, G(4)], party.SubMasters);
        Assert.Equal(PartyResult.Done, party.DecideSetSubMaster(G(1), G(5)).Item1.Result);
        Assert.Equal([G(2), G(5), G(4)], party.SubMasters);
        Assert.Equal([G(2), G(5), G(4)], party.SubMastersInRoster());
    }

    [Fact]
    public void SubMaster_RefusesNonLeader_NonMember_TheLeaderItself_ADuplicate_AndADismissOfANonSubMaster()
    {
        var party = Party(3);
        Assert.Equal(PartyResult.NotLeader, party.DecideSetSubMaster(G(2), G(3)).Item1.Result);
        Assert.Equal(PartyResult.NotMember, party.DecideSetSubMaster(G(1), G(9)).Item1.Result);
        Assert.Equal(PartyResult.NotMember, party.DecideSetSubMaster(G(1), G(1)).Item1.Result);
        party.DecideSetSubMaster(G(1), G(2));
        Assert.Equal(PartyResult.Refused, party.DecideSetSubMaster(G(1), G(2)).Item1.Result);
        Assert.Equal(PartyResult.Refused, party.DecideDismissSubMaster(G(1), G(3)).Item1.Result);
        Assert.Equal(PartyResult.NotMember, party.DecideDismissSubMaster(G(1), G(9)).Item1.Result);
        Assert.Equal(PartyResult.NotLeader, party.DecideDismissSubMaster(G(2), G(2)).Item1.Result);
    }

    // ---- frame move ------------------------------------------------------------------------------------------

    [Fact]
    public void FrameMove_Invalid_ChangesNothingAndSendsNothing()
    {
        var party = Party(2);
        var stranger = G(99);

        foreach (var (moved, occupant, slot) in new (ulong, ulong, byte)[]
        {
            (0, 0, 3), (G(2), 0, 0), (G(2), 0, 6), (stranger, 0, 3), (G(2), stranger, 3),
        })
        {
            var (o, fx) = party.DecideSwapFrameSlots(G(1), moved, occupant, slot);
            Assert.NotEqual(PartyResult.Done, o.Result);
            Assert.Empty(fx);
        }
        Assert.Equal(1, party.FrameSlotOf(G(1)));
        Assert.Equal(2, party.FrameSlotOf(G(2)));
    }

    [Fact]
    public void FrameMove_SwapsOccupied_MovesToEmpty_AndSwapsWhenTheClientSawItEmpty()
    {
        var party = Party(3);

        // C (slot 3) takes B's slot 2: slots swap.
        var (o, fx) = party.DecideSwapFrameSlots(G(1), G(3), G(2), 2);
        Assert.Equal(PartyResult.Done, o.Result);
        Assert.Equal<PartyEffect>(
        [
            new SendToMember(G(1), M(PartyMessageKind.AnsFrameMove)),
            new SendToParty(M(PartyMessageKind.NfyFrameMove, gid: G(3), value: 2, gid2: G(2), value2: 3)),
        ], fx);
        Assert.Equal(2, party.FrameSlotOf(G(3)));
        Assert.Equal(3, party.FrameSlotOf(G(2)));

        // A moves to the empty slot 5.
        (_, fx) = party.DecideSwapFrameSlots(G(1), G(1), 0, 5);
        Assert.Equal(new SendToParty(M(PartyMessageKind.NfyFrameMove, gid: G(1), value: 5, gid2: 0, value2: 0)), fx[1]);
        Assert.Equal(5, party.FrameSlotOf(G(1)));

        // B (slot 3) is sent to slot 2 as "empty"; the server has C there, so they swap.
        (_, fx) = party.DecideSwapFrameSlots(G(2), G(2), 0, 2);
        Assert.Equal<PartyEffect>(
        [
            new SendToMember(G(2), M(PartyMessageKind.AnsFrameMove)),
            new SendToParty(M(PartyMessageKind.NfyFrameMove, gid: G(2), value: 2, gid2: G(3), value2: 3)),
        ], fx);
        Assert.Equal(2, party.FrameSlotOf(G(2)));
        Assert.Equal(3, party.FrameSlotOf(G(3)));
    }

    [Fact]
    public void FrameMove_SlotCapFollowsThePartyType()
    {
        var party = Party(2);
        Assert.Equal(PartyResult.Ignored, party.DecideSwapFrameSlots(G(1), G(2), 0, 6).Item1.Result);
        party.PartyType = PartyAggregate.PartyTypeRaid;
        Assert.Equal(PartyResult.Done, party.DecideSwapFrameSlots(G(1), G(2), 0, 6).Item1.Result);
        Assert.Equal(6, party.FrameSlotOf(G(2)));
    }

    // ---- type change -----------------------------------------------------------------------------------------

    [Fact]
    public void ChangeType_AnswersTheLeader_ThenTheOthers_ThenTheRoster()
    {
        var party = Party(3);

        var (o, fx) = party.DecideChangeType(G(1), PartyAggregate.PartyTypeRaid, onDungeonMap: false);

        Assert.Equal(PartyResult.Done, o.Result);
        Assert.Equal(PartyAggregate.PartyTypeRaid, party.PartyType);
        Assert.Equal<PartyEffect>(
        [
            new SendToMember(G(1), M(PartyMessageKind.AnsTypeChange, value: 2)),
            new SendToPartyExcept(G(1), M(PartyMessageKind.NfyTypeChange, value: 2)),
            new SendRosterUpdate(),
        ], fx);
    }

    [Fact]
    public void ChangeType_OnADungeonMap_IsRefused15719_AndANonLeaderIsDropped()
    {
        var party = Party(3);

        var (o, fx) = party.DecideChangeType(G(1), PartyAggregate.PartyTypeRaid, onDungeonMap: true);

        Assert.Equal(new PartyOutcome(PartyResult.Refused, 15719), o);
        Assert.Equal<PartyEffect>([new SendToMember(G(1), M(PartyMessageKind.AnsTypeChange, code: 15719, value: 2))], fx);
        Assert.Equal(PartyAggregate.PartyTypeNormal, party.PartyType);

        var (o2, fx2) = party.DecideChangeType(G(2), PartyAggregate.PartyTypeRaid, false);
        Assert.Equal(PartyResult.NotLeader, o2.Result);
        Assert.Empty(fx2);
    }

    // ---- helper ----------------------------------------------------------------------------------------------

    private static readonly Dictionary<ulong, byte> Levels = new() { [G(1)] = 50, [G(2)] = 30 };

    [Fact]
    public void Helper_Declare_Answers2902ThenTheRoster_Withdraw_Answers0_AndAnIneligibleOneSendsNothing()
    {
        var party = Party(2); // G(1) level 50, G(2) level 30
        var online = Online(1, 2);

        var (o, fx) = party.DecideSetHelperFlag(G(2), 1, Levels, online);
        Assert.Equal(PartyResult.NotEligible, o.Result);
        Assert.Empty(fx);
        Assert.Empty(party.HelperFlags);

        (o, fx) = party.DecideSetHelperFlag(G(1), 1, Levels, online);
        Assert.Equal(PartyResult.Done, o.Result);
        Assert.Equal(1, party.HelperFlags[G(1)]);
        Assert.Equal<PartyEffect>(
        [
            new SendToMember(G(1), M(PartyMessageKind.AnsHelperDeclare, gid: G(1), value: 1)),
            new SendRosterUpdate(),
        ], fx);

        (o, fx) = party.DecideSetHelperFlag(G(1), 0, Levels, online);
        Assert.Equal(PartyResult.Done, o.Result);
        Assert.Empty(party.HelperFlags);
        Assert.Equal(new SendToMember(G(1), M(PartyMessageKind.AnsHelperDeclare, gid: G(1), value: 0)), fx[0]);
    }

    [Fact]
    public void Helper_NeedsTheFullGap_AndAnOnlineMember()
    {
        var party = Party(2);
        var online = Online(1, 2);
        var nine = new Dictionary<ulong, byte> { [G(1)] = 39, [G(2)] = 30 };
        var ten = new Dictionary<ulong, byte> { [G(1)] = 40, [G(2)] = 30 };

        Assert.Equal(PartyResult.NotEligible, party.DecideSetHelperFlag(G(1), 1, nine, online).Item1.Result);
        Assert.Equal(PartyResult.Done, party.DecideSetHelperFlag(G(1), 1, ten, online).Item1.Result);
        Assert.Equal(PartyResult.NotMember, party.DecideSetHelperFlag(G(1), 1, ten, Online(2)).Item1.Result);
        Assert.Equal(PartyResult.NotMember, party.DecideSetHelperFlag(G(9), 1, ten, online).Item1.Result);
    }
}
