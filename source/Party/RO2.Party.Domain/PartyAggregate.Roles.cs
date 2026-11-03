namespace RO2.Parties.Domain;

public sealed partial class PartyAggregate
{
    /// <summary>
    /// A 1503 request: the leader promotes a member (not itself) to secondary leader. Slots holding a GID that is no
    /// member are cleared first (kept even when the promotion is then refused, as in the host); a duplicate or a full
    /// table is refused without a frame.
    /// </summary>
    public (PartyOutcome, IReadOnlyList<PartyEffect>) DecideSetSubMaster(ulong actorGid, ulong targetGid)
    {
        if (actorGid != LeaderGid) return Res(PartyResult.NotLeader);
        if (!IsMember(targetGid) || targetGid == LeaderGid) return Res(PartyResult.NotMember);

        for (var i = 0; i < _subMasters.Length; i++)
            if (_subMasters[i] != 0 && !IsMember(_subMasters[i])) _subMasters[i] = 0;
        var free = Array.IndexOf(_subMasters, 0UL);
        if (Array.IndexOf(_subMasters, targetGid) >= 0 || free < 0) return Res(PartyResult.Refused);
        _subMasters[free] = targetGid;

        return Res(PartyResult.Done, 0,
            new SendToMember(actorGid, Msg(PartyMessageKind.AnsSubMaster)),
            new SendToParty(Msg(PartyMessageKind.NfySubMaster, gid: targetGid)));
    }

    /// <summary>A 1505 request: the leader removes the secondary leader role from a member.</summary>
    public (PartyOutcome, IReadOnlyList<PartyEffect>) DecideDismissSubMaster(ulong actorGid, ulong targetGid)
    {
        if (actorGid != LeaderGid) return Res(PartyResult.NotLeader);
        if (!IsMember(targetGid)) return Res(PartyResult.NotMember);
        var slot = Array.IndexOf(_subMasters, targetGid);
        if (slot < 0) return Res(PartyResult.Refused);
        _subMasters[slot] = 0;

        return Res(PartyResult.Done, 0,
            new SendToMember(actorGid, Msg(PartyMessageKind.AnsDismissSubMaster)),
            new SendToParty(Msg(PartyMessageKind.NfyDismissSubMaster, gid: targetGid)));
    }

    /// <summary>
    /// A 1504 frame move (any member may send it). The mover takes <paramref name="targetSlot"/>; the occupant named by
    /// <paramref name="occupantGid"/> (or, when it is 0, whoever the server holds in that slot) takes the mover's old slot.
    /// Invalid requests (zero GID or slot, slot above the type's cap, a GID that is no member) change nothing.
    /// </summary>
    public (PartyOutcome, IReadOnlyList<PartyEffect>) DecideSwapFrameSlots(ulong actorGid, ulong movedGid,
        ulong occupantGid, byte targetSlot)
    {
        if (!IsMember(actorGid)) return Res(PartyResult.NotMember);
        if (movedGid == 0 || targetSlot == 0 || targetSlot > MaxMembers) return Res(PartyResult.Ignored);
        if (!IsMember(movedGid)) return Res(PartyResult.NotMember);
        if (occupantGid != 0 && !IsMember(occupantGid)) return Res(PartyResult.NotMember);

        if (occupantGid == 0)
            foreach (var (gid, slot) in _slots)
                if (slot == targetSlot && gid != movedGid) { occupantGid = gid; break; }

        var moverOldSlot = FrameSlotOf(movedGid);
        byte slot1, slot2;
        if (occupantGid != 0)
        {
            slot1 = FrameSlotOf(occupantGid);
            slot2 = moverOldSlot;
            _slots[movedGid] = slot1;
            _slots[occupantGid] = slot2;
        }
        else
        {
            slot1 = targetSlot;
            slot2 = 0;
            _slots[movedGid] = slot1;
        }

        return Res(PartyResult.Done, 0,
            new SendToMember(actorGid, Msg(PartyMessageKind.AnsFrameMove)),
            new SendToParty(Msg(PartyMessageKind.NfyFrameMove, gid: movedGid, value: slot1, gid2: occupantGid, value2: slot2)));
    }

    /// <summary>
    /// A 1497 type change by the leader. <paramref name="onDungeonMap"/> is the host's precomputed map-type flag for the
    /// leader: on a dungeon map the change is refused with 15719 and nothing else is sent.
    /// </summary>
    public (PartyOutcome, IReadOnlyList<PartyEffect>) DecideChangeType(ulong actorGid, byte partyType, bool onDungeonMap)
    {
        if (actorGid != LeaderGid) return Res(PartyResult.NotLeader);
        if (onDungeonMap)
            return Res(PartyResult.Refused, CodeTypeChangeOnDungeonMap, new SendToMember(actorGid,
                Msg(PartyMessageKind.AnsTypeChange, code: CodeTypeChangeOnDungeonMap, value: partyType)));

        PartyType = partyType;
        return Res(PartyResult.Done, 0,
            new SendToMember(actorGid, Msg(PartyMessageKind.AnsTypeChange, value: partyType)),
            new SendToPartyExcept(actorGid, Msg(PartyMessageKind.NfyTypeChange, value: partyType)),
            new SendRosterUpdate());
    }

    /// <summary>
    /// A 1614 Helper declaration (<paramref name="flag"/> 1 declared, 0 withdrawn). The declarer must be an online member.
    /// A declaration needs at least one other member <see cref="HelperLevelGap"/> or more levels below it;
    /// <paramref name="memberLevels"/> holds every member's known level.
    /// </summary>
    public (PartyOutcome, IReadOnlyList<PartyEffect>) DecideSetHelperFlag(ulong gid, byte flag,
        IReadOnlyDictionary<ulong, byte> memberLevels, IReadOnlySet<ulong> onlineSet)
    {
        if (!IsMember(gid) || !onlineSet.Contains(gid)) return Res(PartyResult.NotMember);

        if (flag == 0) _helpers.Remove(gid);
        else
        {
            var level = memberLevels.GetValueOrDefault(gid);
            var eligible = _members.Any(m => m != gid && memberLevels.TryGetValue(m, out var other)
                && level >= other + HelperLevelGap);
            if (!eligible) return Res(PartyResult.NotEligible);
            _helpers[gid] = flag;
        }
        _lapsedHelpers.Remove(gid);

        return Res(PartyResult.Done, 0,
            new SendToMember(gid, Msg(PartyMessageKind.AnsHelperDeclare, gid: gid, value: flag)),
            new SendRosterUpdate());
    }
}
