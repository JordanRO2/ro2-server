namespace RO2.Parties.Domain;

public sealed partial class PartyAggregate
{
    /// <summary>
    /// A 1382 invitation. <paramref name="inviterParty"/> is the inviter's party (null when it has none);
    /// <paramref name="open"/> the target's open invitation; <paramref name="targetReachable"/> whether the target has a
    /// live session. <see cref="PartyResult.InviteOpened"/> carries the invitation the host stores.
    /// </summary>
    public static (PartyOutcome, IReadOnlyList<PartyEffect>) DecideInvite(ulong inviterGid, string inviterName,
        ulong targetGid, string requestedName, PartyAggregate? inviterParty, bool targetInParty, bool targetReachable,
        PartyInvite? open, DateTime nowUtc)
    {
        if (targetGid == inviterGid) return Res(PartyResult.Ignored);
        var type = inviterParty?.PartyType ?? PartyTypeNormal;

        (PartyOutcome, IReadOnlyList<PartyEffect>) Refused(int code) => Res(PartyResult.Refused, code,
            new SendToMember(inviterGid, Msg(PartyMessageKind.InviteResult, code: code, text: requestedName, value: type)));

        if (inviterParty != null && inviterParty.Members.Count >= inviterParty.MaxMembers) return Refused(CodePartyFull);
        if (targetInParty) return Refused(CodeTargetInParty);
        if (!targetReachable) return Res(PartyResult.Ignored);
        if (open != null && open.IsOpen(nowUtc)) return Refused(CodeAlreadyInvited);

        var invite = new PartyInvite(inviterGid, requestedName, nowUtc, inviterParty?.Id ?? 0);
        return Res(new PartyOutcome(PartyResult.InviteOpened, Invite: invite),
            new SendToMember(targetGid, Msg(PartyMessageKind.InviteDialog, gid: inviterGid, text: inviterName, value: type)));
    }

    /// <summary>
    /// A 1383 answer. <paramref name="pending"/> is the responder's open invitation; <paramref name="inviterGid"/> the
    /// inviter's GID while it is online (null otherwise). No time check: an invitation is answerable until consumed.
    /// </summary>
    public static (PartyOutcome, IReadOnlyList<PartyEffect>) DecideAnswer(PartyInvite? pending, ulong responderGid,
        ulong? inviterGid, ushort answer, ulong claimedInviterGid, string inviterName, byte inviterPartyType)
    {
        if (pending == null) return Res(PartyResult.Ignored);
        if (inviterGid == null)
            return answer == AnswerAccept
                ? Res(PartyResult.InviteConsumed, 0,
                    new SendToMember(responderGid, Msg(PartyMessageKind.Error, code: CodeInviterNotFound, text: inviterName)))
                : Res(PartyResult.InviteConsumed);
        if (inviterGid != claimedInviterGid) return Res(PartyResult.Ignored);

        switch (answer)
        {
            case AnswerAccept:
                return Res(PartyResult.JoinNext);
            case AnswerDecline or AnswerTimedOut:
                var code = answer == AnswerDecline ? CodeDeclined : CodeTimedOut;
                return Res(PartyResult.InviteConsumed, code,
                    new SendToMember(responderGid, Msg(PartyMessageKind.Error, code: code, text: inviterName)),
                    new SendToMember(inviterGid.Value, Msg(PartyMessageKind.InviteResult, code: code,
                        text: pending.RequestedName, value: inviterPartyType)));
            default:
                return Res(PartyResult.InviteConsumed);
        }
    }

    /// <summary>
    /// An accepted invitation: the joiner joins the inviter's party, founded with the inviter as leader and founder when
    /// it has none. <paramref name="onlineSet"/> holds the GIDs whose member record is bound to a live session, so a
    /// joiner the party holds as an offline member is taken back in place (even at the cap). A refusal sends the 2348
    /// error to the joiner.
    /// </summary>
    public static (PartyOutcome, IReadOnlyList<PartyEffect>) DecideJoin(PartyAggregate? inviterParty, uint newPartyId,
        ulong inviterGid, string inviterName, ulong joinerGid, string joinerName, bool joinerInParty,
        uint invitationPartyId, IReadOnlySet<ulong> onlineSet, DateTime nowUtc)
    {
        (PartyOutcome, IReadOnlyList<PartyEffect>) Refused(int code) => Res(PartyResult.Refused, code,
            new SendToMember(joinerGid, Msg(PartyMessageKind.Error, code: code, text: inviterName)));

        if (joinerInParty) return Refused(CodeTargetInParty);
        if (invitationPartyId != 0 && inviterParty?.Id != invitationPartyId) return Refused(CodeInviterNotFound);

        var created = inviterParty == null;
        var party = inviterParty ?? new PartyAggregate(newPartyId, inviterGid, nowUtc);
        if (created || !party.IsMember(joinerGid))
        {
            if (!created && party._members.Count >= party.MaxMembers) return Refused(CodePartyFull);
            party.AddMember(joinerGid);
        }
        else
        {
            // Decision: a joiner whose GID is already an ONLINE member (a duplicate login) is refused as in a party; the
            // host would have added a second record for it.
            if (onlineSet.Contains(joinerGid)) return Refused(CodeTargetInParty);
            party._lockedSince.Remove(joinerGid);
        }

        var accepted = new SendToMember(inviterGid, Msg(PartyMessageKind.InviteResult, text: joinerName, value: party.PartyType));
        return Res(new PartyOutcome(created ? PartyResult.Created : PartyResult.Done, Party: party),
            created
                ? [accepted, new SendRosterCreate(null)]
                : [new SendJoinDelta(joinerGid), accepted, new SendRosterCreate(joinerGid)]);
    }
}
