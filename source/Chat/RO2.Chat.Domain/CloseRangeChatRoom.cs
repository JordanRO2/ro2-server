namespace RO2.Chats.Domain;

/// <summary>A member of a close-range chat room: the wire uid (the 23001 messenger uid), the database id and the name.</summary>
public readonly record struct CloseRangeMember(ulong Uid, uint CharacterId, string Name);

/// <summary>What leaving a close-range room did.</summary>
public enum CloseRangeLeaveOutcome
{
    /// <summary>No member list for that room here (nobody joined it through this process).</summary>
    UnknownRoom,
    /// <summary>The room is known but the leaver is not in it.</summary>
    NotAMember,
    /// <summary>A member left; the others were told (23520).</summary>
    MemberLeft,
    /// <summary>The owner left an empty room: the room is closed here.</summary>
    OwnerLeft,
    /// <summary>The owner left a room others are still in: the lowest remaining uid is its owner now, the room
    /// stays open, and whoever stayed was told (23520).</summary>
    HandedOff,
}

/// <summary>What a departure did: the outcome, the member count left (owner included; 0 unless MemberLeft or
/// HandedOff) and, for a hand-off, the new owner's database id (0 otherwise).</summary>
public readonly record struct CloseRangeDeparture(CloseRangeLeaveOutcome Outcome, int MemberCount, uint NewMasterCharacterId);

/// <summary>Which wire message an effect becomes; the host owns the opcode and the bytes.</summary>
public enum CloseRangeMessage
{
    JoinAnswer,      // 23538 (Code = result)
    MemberList,      // 23517
    JoinUser,        // 23516 (Uid, Text = name)
    ExitMemberInfo,  // 23520 (Uid = leaver)
}

/// <summary>One message a decision queues, in send order. <see cref="Recipient"/> is the member uid it goes to;
/// null means the requester (the joiner, who is not yet a member when it is refused).</summary>
public readonly record struct CloseRangeEffect(ulong? Recipient, CloseRangeMessage Message, ulong Uid = 0, ushort Code = 0, string Text = "");

/// <summary>
/// A close-range ("local") chat room: id-only state, no session. Every Decide mutates the state and returns the
/// ordered effects; the host resolves uids to connections and builds the wire bytes.
/// </summary>
public sealed class CloseRangeChatRoom
{
    /// <summary>23538 result 0: joined.</summary>
    public const ushort ResultJoined = 0;
    /// <summary>23538 result 10: string 9726 "Cannot find channel room."</summary>
    public const ushort ResultRoomNotFound = 10;
    /// <summary>23538 result 19: string 9736 "Please enter your password correctly."</summary>
    public const ushort ResultWrongPassword = 19;
    /// <summary>23538 result 23: string 9516 "Already participating in the target channel."</summary>
    public const ushort ResultAlreadyMember = 23;
    /// <summary>23538 result 29: string 9758 "Cannot enter because the room is full."</summary>
    public const ushort ResultRoomFull = 29;

    /// <summary>Most rooms one character may be in at once. The client keeps a single current room;
    /// 3 leaves headroom for a hand-off. Over it the join is answered 23 (already participating).</summary>
    public const int MaxRoomsPerCharacter = 3;

    // Keyed and ordered by uid, as the private rooms list everyone after the master.
    private readonly SortedDictionary<ulong, CloseRangeMember> _members = new();

    public CloseRangeChatRoom(byte[] guidBytes, byte capacity, byte[] password, CloseRangeMember owner)
    {
        GuidBytes = guidBytes;
        Capacity = capacity;
        Password = password;
        OwnerUid = owner.Uid;
        _members[owner.Uid] = owner;
    }

    public byte[] GuidBytes { get; }
    public byte Capacity { get; }
    public byte[] Password { get; }
    /// <summary>The opener, or the member a hand-off made owner since.</summary>
    public ulong OwnerUid { get; private set; }

    /// <summary>Members in ascending uid order.</summary>
    public IReadOnlyDictionary<ulong, CloseRangeMember> Members => _members;

    public bool HasCharacter(uint characterId) => _members.Values.Any(m => m.CharacterId == characterId);

    /// <summary>The members as 23517 lists them: the owner first, then the others in uid order.</summary>
    public List<(ulong Uid, string Name)> OwnerFirst()
    {
        var list = new List<(ulong Uid, string Name)>(_members.Count);
        if (_members.TryGetValue(OwnerUid, out var owner)) list.Add((owner.Uid, owner.Name));
        foreach (var m in _members.Values)
            if (m.Uid != OwnerUid) list.Add((m.Uid, m.Name));
        return list;
    }

    /// <summary>
    /// 23020 on <paramref name="room"/> (null: the host holds none). Checked in this order: no room (10), the uid
    /// is already a member (23), the character holds <see cref="MaxRoomsPerCharacter"/> rooms (23), the password
    /// (19), a full room (29). The 23538 answer goes to the requester in every case; on success the joiner gets
    /// MemberList, then every member (the joiner included) JoinUser.
    /// </summary>
    public static (ushort Result, List<CloseRangeEffect> Effects) Join(CloseRangeChatRoom? room, CloseRangeMember joiner,
        ReadOnlySpan<byte> password, int roomsHeld)
    {
        var result = room is null ? ResultRoomNotFound
            : room._members.ContainsKey(joiner.Uid) ? ResultAlreadyMember
            : roomsHeld >= MaxRoomsPerCharacter ? ResultAlreadyMember
            : !password.SequenceEqual(room.Password) ? ResultWrongPassword
            : room._members.Count >= room.Capacity ? ResultRoomFull
            : ResultJoined;
        var effects = new List<CloseRangeEffect> { new(null, CloseRangeMessage.JoinAnswer, Code: result) };
        if (result != ResultJoined) return (result, effects);

        room!._members[joiner.Uid] = joiner;
        effects.Add(new(null, CloseRangeMessage.MemberList));
        foreach (var m in room._members.Values)
            effects.Add(new(m.Uid, CloseRangeMessage.JoinUser, joiner.Uid, Text: joiner.Name));
        return (result, effects);
    }

    /// <summary>
    /// 23022: the member whose database id is <paramref name="characterId"/> leaves. NotAMember (no effects) if it
    /// is not in the room; otherwise as <see cref="Depart"/>.
    /// </summary>
    public (CloseRangeDeparture Departure, List<CloseRangeEffect> Effects) Leave(uint characterId)
    {
        var leaver = _members.Values.Where(m => m.CharacterId == characterId).Select(m => (CloseRangeMember?)m).FirstOrDefault();
        return leaver is not { } gone
            ? (new CloseRangeDeparture(CloseRangeLeaveOutcome.NotAMember, 0, 0), [])
            : Depart(gone);
    }

    /// <summary>
    /// <paramref name="goneUid"/>'s connection is closing. The members in <paramref name="deadOthers"/> (their
    /// connections are gone too; the owner and the leaver are never dropped here) are removed first, so neither the
    /// notice nor the ownership goes to a dead one. A leaver other than the owner is dropped without a notice
    /// (MemberLeft, no effects); the owner departs as in <see cref="Leave"/>.
    /// </summary>
    public (CloseRangeDeparture Departure, List<CloseRangeEffect> Effects) Close(ulong goneUid, IReadOnlySet<ulong> deadOthers)
    {
        foreach (var dead in deadOthers)
            if (dead != goneUid && dead != OwnerUid) _members.Remove(dead);
        var gone = _members[goneUid];
        if (goneUid != OwnerUid)
        {
            _members.Remove(goneUid);
            return (new CloseRangeDeparture(CloseRangeLeaveOutcome.MemberLeft, _members.Count, 0), []);
        }
        return Depart(gone);
    }

    /// <summary>Drops members the host found disconnected, without a notice.</summary>
    public void Drop(IEnumerable<ulong> uids)
    {
        foreach (var uid in uids) _members.Remove(uid);
    }

    /// <summary>
    /// <paramref name="gone"/> leaves: 23520 to every member who stays, then, when it was the owner, the room
    /// closes if nobody is left (OwnerLeft: the host drops it) or passes to the lowest remaining uid (the room
    /// keeps its GUID, capacity and password).
    /// </summary>
    private (CloseRangeDeparture, List<CloseRangeEffect>) Depart(CloseRangeMember gone)
    {
        _members.Remove(gone.Uid);
        var effects = new List<CloseRangeEffect>();
        foreach (var m in _members.Values)
            effects.Add(new(m.Uid, CloseRangeMessage.ExitMemberInfo, gone.Uid));

        if (gone.Uid != OwnerUid)
            return (new CloseRangeDeparture(CloseRangeLeaveOutcome.MemberLeft, _members.Count, 0), effects);
        if (_members.Count == 0)
            return (new CloseRangeDeparture(CloseRangeLeaveOutcome.OwnerLeft, 0, 0), effects);
        OwnerUid = _members.Keys.First(); // lowest remaining uid (SortedDictionary)
        return (new CloseRangeDeparture(CloseRangeLeaveOutcome.HandedOff, _members.Count, _members[OwnerUid].CharacterId), effects);
    }
}
