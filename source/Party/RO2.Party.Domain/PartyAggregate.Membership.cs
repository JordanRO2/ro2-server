namespace RO2.Parties.Domain;

public sealed partial class PartyAggregate
{
    /// <summary>
    /// A member leaves. It is removed before anything is sent, so the remaining members (never the leaver) get the 2360
    /// of a voluntary leave. One member left ends the party (<see cref="DecideDisband"/>); otherwise a leaving leader is
    /// succeeded by the first online member (the first member when none is online) and the roster follows.
    /// </summary>
    public (PartyOutcome, IReadOnlyList<PartyEffect>) DecideLeave(ulong leaverGid, PartyLeaveReason reason,
        IReadOnlySet<ulong> onlineSet)
    {
        if (!IsMember(leaverGid)) return Res(PartyResult.NotMember);

        _members.Remove(leaverGid);
        _slots.Remove(leaverGid);
        _helpers.Remove(leaverGid);
        _lapsedHelpers.Remove(leaverGid);
        var subSlot = Array.IndexOf(_subMasters, leaverGid);
        if (subSlot >= 0) _subMasters[subSlot] = 0;
        // Decision: the leaver's lockedSince entry is left in place, as the host does; it is harmless (no member to drop).

        List<PartyEffect> effects = [];
        if (reason == PartyLeaveReason.Left)
            effects.Add(new SendToParty(Msg(PartyMessageKind.Out, gid: leaverGid, value: OutKindRemoved)));

        if (_members.Count <= 1)
        {
            effects.AddRange(DecideDisband(reason == PartyLeaveReason.Kicked ? DestroyCopiesAfterKick : DestroyCopiesOnDisband,
                onlineSet).Item2);
            return (new PartyOutcome(PartyResult.Disbanded), effects);
        }

        if (LeaderGid == leaverGid)
        {
            LeaderGid = _members.FirstOrDefault(onlineSet.Contains, _members[0]);
            effects.Add(new SendToParty(Msg(PartyMessageKind.LeaderChange, gid: LeaderGid)));
        }
        effects.Add(new SendRosterUpdate());
        return (new PartyOutcome(PartyResult.Done), effects);
    }

    /// <summary>The party ends: each online survivor gets <paramref name="copies"/> 2358 and the aggregate is marked disbanded.</summary>
    public (PartyOutcome, IReadOnlyList<PartyEffect>) DecideDisband(int copies, IReadOnlySet<ulong> onlineSet)
    {
        IsDisbanded = true;
        return Res(PartyResult.Disbanded, 0, new SendDestroy(copies, [.. _members.Where(onlineSet.Contains)]));
    }

    /// <summary>
    /// A 1388 expulsion by the leader. A GID that is no member is answered with 15712; a party of two or fewer sends the
    /// kicker a 2358 first (the party ends with the kick, whose own destroy is a second one); the others get 2366, the
    /// expelled member (when online) 2367, then the member leaves as <see cref="PartyLeaveReason.Kicked"/>.
    /// </summary>
    public (PartyOutcome, IReadOnlyList<PartyEffect>) DecideKick(ulong actorGid, ulong targetGid, IReadOnlySet<ulong> onlineSet)
    {
        if (actorGid != LeaderGid) return Res(PartyResult.NotLeader);
        if (targetGid == 0) return Res(PartyResult.Ignored);
        if (!IsMember(targetGid))
            return Res(PartyResult.Refused, CodeExpulsionNotMember,
                new SendToMember(actorGid, Msg(PartyMessageKind.ExpulsionAck, code: CodeExpulsionNotMember)));
        if (targetGid == actorGid) return Res(PartyResult.Ignored);

        List<PartyEffect> effects = [];
        if (_members.Count <= 2) effects.Add(new SendToMember(actorGid, Msg(PartyMessageKind.Destroy)));
        effects.Add(new SendToMember(actorGid, Msg(PartyMessageKind.ExpulsionAck)));
        effects.Add(new SendToPartyExcept(targetGid, Msg(PartyMessageKind.ExpulsionNotify, gid: targetGid)));
        if (onlineSet.Contains(targetGid)) effects.Add(new SendLayoff(targetGid));

        var (outcome, leave) = DecideLeave(targetGid, PartyLeaveReason.Kicked, onlineSet);
        effects.AddRange(leave);
        return (outcome, effects);
    }

    /// <summary>
    /// The leader hands the party to the first online member other than <paramref name="departedGid"/> (a locked leader
    /// dropped by expiry). No online successor leaves the leader unchanged.
    /// </summary>
    public (PartyOutcome, IReadOnlyList<PartyEffect>) DecideSucceedLeader(ulong departedGid, IReadOnlySet<ulong> onlineSet)
    {
        if (LeaderGid != departedGid) return Res(PartyResult.Ignored);
        var successor = _members.FirstOrDefault(g => g != departedGid && onlineSet.Contains(g));
        if (successor == 0) return Res(PartyResult.NoSuccessor);
        LeaderGid = successor;
        return Res(new PartyOutcome(PartyResult.Done, Gid: successor),
            new SendToParty(Msg(PartyMessageKind.LeaderChange, gid: successor)));
    }

    /// <summary>
    /// A 1387 request: the leader hands leadership to an online member other than itself. <paramref name="refusedAsWoe"/>
    /// is the host's precomputed WoE-map refusal for that member.
    /// </summary>
    public (PartyOutcome, IReadOnlyList<PartyEffect>) DecideChangeLeader(ulong actorGid, ulong newLeaderGid,
        IReadOnlySet<ulong> onlineSet, bool refusedAsWoe)
    {
        if (actorGid != LeaderGid) return Res(PartyResult.NotLeader);
        if (newLeaderGid == actorGid || !IsMember(newLeaderGid) || !onlineSet.Contains(newLeaderGid))
            return Res(PartyResult.NotMember);
        if (refusedAsWoe)
            return Res(PartyResult.Refused, CodeChangeMasterWoeMap,
                new SendToMember(actorGid, Msg(PartyMessageKind.ChangeMasterAck, code: CodeChangeMasterWoeMap)));

        LeaderGid = newLeaderGid;
        return Res(new PartyOutcome(PartyResult.Done, Gid: newLeaderGid),
            new SendToMember(actorGid, Msg(PartyMessageKind.ChangeMasterAck)),
            new SendToParty(Msg(PartyMessageKind.LeaderChange, gid: newLeaderGid)),
            new SendRosterUpdate());
    }

    /// <summary>A member's session ended: it is locked in place (2360 kind 1 to the others) and the expiry sweep is scheduled.</summary>
    public (PartyOutcome, IReadOnlyList<PartyEffect>) DecideMemberLocked(ulong gid, DateTime nowUtc)
    {
        if (!IsMember(gid)) return Res(PartyResult.NotMember);
        _lockedSince[gid] = nowUtc;
        return Res(PartyResult.Done, 0, new ScheduleLockSweep(),
            new SendToPartyExcept(gid, Msg(PartyMessageKind.Out, gid: gid, value: OutKindLocked)));
    }

    /// <summary>Pure predicate: the member is locked and nowUtc - lockedSince >= <see cref="LockedMemberLifetime"/>. The sweep timer is the host's.</summary>
    public bool IsLockExpired(ulong gid, DateTime nowUtc)
        => _lockedSince.TryGetValue(gid, out var since) && nowUtc - since >= LockedMemberLifetime;

    /// <summary>The locked members whose lock has expired at the given time.</summary>
    public IReadOnlyList<ulong> ExpiredLocks(DateTime nowUtc)
        => [.. _lockedSince.Keys.Where(g => IsLockExpired(g, nowUtc))];

    /// <summary>
    /// Drops a member whose lock expired. The lock entry is consumed; a member that is a member and still offline hands
    /// leadership on first (2365), then leaves as <see cref="PartyLeaveReason.Left"/>.
    /// </summary>
    public (PartyOutcome, IReadOnlyList<PartyEffect>) DecideDropLocked(ulong gid, DateTime nowUtc, IReadOnlySet<ulong> onlineSet)
    {
        if (!IsLockExpired(gid, nowUtc)) return Res(PartyResult.Ignored);
        _lockedSince.Remove(gid);
        if (!IsMember(gid) || onlineSet.Contains(gid)) return Res(PartyResult.Ignored);

        List<PartyEffect> effects = [];
        effects.AddRange(DecideSucceedLeader(gid, onlineSet).Item2);
        var (outcome, leave) = DecideLeave(gid, PartyLeaveReason.Left, onlineSet);
        effects.AddRange(leave);
        return (outcome, effects);
    }
}
