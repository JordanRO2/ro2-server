namespace RO2.Chats.Domain;

public enum JoinOutcome { Joined, WrongPassword, Full, AlreadyInRoom }

/// <summary>A member of a private chat room: the wire uid (the full 64-bit 23001 uid, never truncated), the
/// database id (what 23013's caller identifies it by) and the name.</summary>
public readonly record struct PrivateRoomMember(ulong Uid, uint CharacterId, string Name);

/// <summary>Which wire message an effect becomes; the host owns the opcode and the bytes.</summary>
public enum PrivateRoomMessage
{
    JoinRefusal,         // 23515 [code] refusal record
    JoinAnswer,          // 23515 the room record
    JoinUser,            // 23516 (Uid, Text = name)
    MemberList,          // 23517
    ExitMemberInfo,      // 23520 (Uid = leaver)
    ChangeRoomMaster,    // 23533 (Uid = new master)
    LogoutPlayer,        // 23504 (Uid = gone member)
    InviteResult,        // 23525 (Code)
    InviteInRoomMember,  // 23526 (Uid, Text = name)
    InviteRoomInfo,      // 23532
    KickResult,          // 23522 (Code)
    KickToUser,          // 23524
    KickUser,            // 23523 (Uid = kicked)
    ChangePassAnswer,    // 23521 (Code, Text = password echoed)
}

/// <summary>One message a decision queues, in send order. <see cref="Recipient"/> is the member uid it goes to;
/// null means the requester (the connection that asked, which need not be a member).</summary>
public readonly record struct PrivateRoomEffect(ulong? Recipient, PrivateRoomMessage Message, ulong Uid = 0, ushort Code = 0, string Text = "");

/// <summary>
/// A user-created ("private") chat room: id-only state, no session. Every Decide mutates the state and returns
/// the ordered effects; the host resolves uids to connections and builds the wire bytes. The effect order is
/// the order the old host code sent in.
/// </summary>
public sealed class PrivateRoom
{
    /// <summary>23517's u32 capacity field, 100.</summary>
    public const int MaxMembers = 100;
    /// <summary>Private rooms one client can hold at once (its room-slot bitset has 10 entries).</summary>
    public const int ClientRoomSlots = 10;
    /// <summary>The longest room name the 23532 reader keeps.</summary>
    public const int InviteRoomNameMaxChars = 50;
    /// <summary>The longest member name the client's member reader keeps.</summary>
    public const int MemberNameMaxChars = 20;
    /// <summary>23014's password: at most 4 digits.</summary>
    public const int PasswordMaxChars = 4;

    public const ushort ResultOk = 0;
    /// <summary>9512 "Target does not exist."</summary>
    public const ushort ResultTargetMissing = 1;
    /// <summary>9722 "The maximum number of channels that can be generated has been exceeded."</summary>
    public const ushort ResultRoomLimit = 9;
    /// <summary>9726 "Cannot find channel room."</summary>
    public const ushort ResultNoRoom = 10;
    /// <summary>9551 "Channel passwords can only be 4 numbers."</summary>
    public const ushort ResultBadPassword = 12;
    /// <summary>9518 "You do not have the authorization to do so"</summary>
    public const ushort ResultNotMaster = 15;
    /// <summary>9521 "Option only available in channels you are participating."</summary>
    public const ushort ResultNotMember = 17;
    /// <summary>9574 "The character name you've entered does not exist."</summary>
    public const ushort ResultNoSuchCharacter = 18;
    /// <summary>9736 "Please enter your password correctly." (no retail frame)</summary>
    public const ushort ResultWrongPassword = 19;
    /// <summary>9516 "Already participating in the target channel."</summary>
    public const ushort ResultAlreadyInRoom = 23;
    /// <summary>9735 "Private channel. Please enter your password." (no retail frame)</summary>
    public const ushort ResultPasswordRequired = 26;
    /// <summary>9758 "Cannot enter because the room is full."</summary>
    public const ushort ResultRoomFull = 29;

    private readonly SortedDictionary<ulong, PrivateRoomMember> _members = new();

    public PrivateRoom(Guid id, string name, string password, ulong masterUid)
    {
        Id = id;
        GuidBytes = id.ToByteArray();
        Name = name;
        Password = password;
        MasterUid = masterUid;
    }

    public Guid Id { get; }
    public byte[] GuidBytes { get; }
    public string Name { get; }
    public string Password { get; private set; }
    public ulong MasterUid { get; private set; }

    /// <summary>Members in ascending uid order.</summary>
    public IReadOnlyDictionary<ulong, PrivateRoomMember> Members => _members;
    public bool IsEmpty => _members.Count == 0;

    /// <summary>
    /// 23011 on a room that exists (or was just created with the joiner as master). Checked in this order: the
    /// uid is already a member (23), the password (26 empty / 19 wrong), a full room (29). A refusal is the
    /// lone JoinRefusal to the requester; success is JoinAnswer to the requester, JoinUser to every member
    /// including the joiner, then MemberList to the requester.
    /// </summary>
    public (JoinOutcome Outcome, List<PrivateRoomEffect> Effects) Join(ulong uid, uint characterId, string memberName, string password)
    {
        var effects = new List<PrivateRoomEffect>();
        var (outcome, code) = _members.ContainsKey(uid) ? (JoinOutcome.AlreadyInRoom, ResultAlreadyInRoom)
            : !string.Equals(Password, password, StringComparison.Ordinal)
                ? (JoinOutcome.WrongPassword, password.Length == 0 ? ResultPasswordRequired : ResultWrongPassword)
            : _members.Count >= MaxMembers ? (JoinOutcome.Full, ResultRoomFull)
            : (JoinOutcome.Joined, ResultOk);
        if (outcome != JoinOutcome.Joined)
        {
            effects.Add(new(null, PrivateRoomMessage.JoinRefusal, Code: code));
            return (outcome, effects);
        }

        _members[uid] = new PrivateRoomMember(uid, characterId, memberName);
        effects.Add(new(null, PrivateRoomMessage.JoinAnswer));
        foreach (var m in _members.Values)
            effects.Add(new(m.Uid, PrivateRoomMessage.JoinUser, uid, Text: memberName));
        effects.Add(new(null, PrivateRoomMessage.MemberList));
        return (JoinOutcome.Joined, effects);
    }

    /// <summary>23017: the member list for the requester.</summary>
    public List<PrivateRoomEffect> MemberList() => [new(null, PrivateRoomMessage.MemberList)];

    /// <summary>
    /// 23013: the member whose database id is <paramref name="characterId"/> leaves. False (no effects) if it is
    /// not a member. The remaining members get ExitMemberInfo under the leaver's wire uid, plus ChangeRoomMaster
    /// if the master left (the lowest remaining uid takes over).
    /// </summary>
    public (bool Left, List<PrivateRoomEffect> Effects) Leave(uint characterId)
    {
        var effects = new List<PrivateRoomEffect>();
        var leaver = _members.Values.Where(m => m.CharacterId == characterId).Select(m => (PrivateRoomMember?)m).FirstOrDefault();
        if (leaver is not { } gone) return (false, effects);
        _members.Remove(gone.Uid);
        foreach (var m in _members.Values)
            effects.Add(new(m.Uid, PrivateRoomMessage.ExitMemberInfo, gone.Uid));
        AfterDeparture(masterLeft: MasterUid == gone.Uid, effects);
        return (true, effects);
    }

    /// <summary>
    /// Drop the members in <paramref name="disconnected"/> (their connection is gone) and announce each to the
    /// rest with LogoutPlayer, then the master hand-off. Empty set: nothing changes.
    /// </summary>
    public List<PrivateRoomEffect> Depart(IReadOnlySet<ulong> disconnected)
    {
        var effects = new List<PrivateRoomEffect>();
        var gone = _members.Values.Where(m => disconnected.Contains(m.Uid)).ToList();
        if (gone.Count == 0) return effects;
        foreach (var g in gone)
            _members.Remove(g.Uid);
        foreach (var g in gone)
            foreach (var m in _members.Values)
                effects.Add(new(m.Uid, PrivateRoomMessage.LogoutPlayer, g.Uid));
        AfterDeparture(masterLeft: gone.Any(g => g.Uid == MasterUid), effects);
        return effects;
    }

    /// <summary>
    /// 23016: the master <paramref name="inviterUid"/> invites <paramref name="invitee"/> (null when no online
    /// messenger character has the name). <paramref name="inviteeRoomsHeld"/> is how many rooms already list the
    /// invitee. InviteResult goes to the requester in every case; on success the members already in get
    /// InviteInRoomMember, the invitee joins and gets InviteRoomInfo.
    /// </summary>
    public static (ushort Result, List<PrivateRoomEffect> Effects) Invite(PrivateRoom? room, ulong inviterUid, PrivateRoomMember? invitee, int inviteeRoomsHeld)
    {
        ushort result = room is null ? ResultNoRoom
            : !room._members.ContainsKey(inviterUid) ? ResultNotMember
            : room.MasterUid != inviterUid ? ResultNotMaster
            : invitee is not { } who ? ResultNoSuchCharacter
            : room._members.ContainsKey(who.Uid) ? ResultAlreadyInRoom
            : room._members.Count >= MaxMembers ? ResultRoomFull
            : inviteeRoomsHeld >= ClientRoomSlots ? ResultRoomLimit
            : ResultOk;
        var effects = new List<PrivateRoomEffect> { new(null, PrivateRoomMessage.InviteResult, Code: result) };
        if (result != ResultOk) return (result, effects);

        var added = invitee!.Value;
        foreach (var m in room!._members.Values)
            effects.Add(new(m.Uid, PrivateRoomMessage.InviteInRoomMember, added.Uid, Text: added.Name));
        room._members[added.Uid] = added;
        effects.Add(new(added.Uid, PrivateRoomMessage.InviteRoomInfo));
        return (ResultOk, effects);
    }

    /// <summary>
    /// 23015: the master expels <paramref name="targetUid"/>. KickResult goes to the requester in every case;
    /// on success the target gets KickToUser and every remaining member (the kicker included) KickUser.
    /// </summary>
    public static (ushort Result, List<PrivateRoomEffect> Effects) Kick(PrivateRoom? room, ulong kickerUid, ulong targetUid)
    {
        PrivateRoomMember target = default;
        ushort result = room is null ? ResultNoRoom
            : !room._members.ContainsKey(kickerUid) ? ResultNotMember
            : room.MasterUid != kickerUid ? ResultNotMaster
            : targetUid == kickerUid || !room._members.TryGetValue(targetUid, out target) ? ResultTargetMissing
            : ResultOk;
        var effects = new List<PrivateRoomEffect> { new(null, PrivateRoomMessage.KickResult, Code: result) };
        if (result != ResultOk) return (result, effects);

        room!._members.Remove(targetUid);
        effects.Add(new(target.Uid, PrivateRoomMessage.KickToUser));
        foreach (var m in room._members.Values)
            effects.Add(new(m.Uid, PrivateRoomMessage.KickUser, targetUid));
        room.AfterDeparture(masterLeft: false, effects);
        return (ResultOk, effects);
    }

    /// <summary>23014: the master sets the password. ChangePassAnswer goes to the requester only, echoing the
    /// password on success and an empty one on a refusal.</summary>
    public static (ushort Result, List<PrivateRoomEffect> Effects) ChangePassword(PrivateRoom? room, ulong uid, string password)
    {
        ushort result = room is null ? ResultNoRoom
            : !room._members.ContainsKey(uid) ? ResultNotMember
            : room.MasterUid != uid ? ResultNotMaster
            : !IsValidPassword(password) ? ResultBadPassword
            : ResultOk;
        if (result == ResultOk) room!.Password = password;
        return (result, [new(null, PrivateRoomMessage.ChangePassAnswer, Code: result, Text: result == ResultOk ? password : "")]);
    }

    /// <summary>1 to <see cref="PasswordMaxChars"/> decimal digits (string 9551).</summary>
    private static bool IsValidPassword(string password)
        => password.Length is >= 1 and <= PasswordMaxChars && password.All(char.IsAsciiDigit);

    /// <summary>When the master left and members remain, the lowest remaining uid takes over and the rest are told.
    /// An empty room produces nothing: the host drops it from its registry.</summary>
    private void AfterDeparture(bool masterLeft, List<PrivateRoomEffect> effects)
    {
        if (_members.Count == 0 || !masterLeft) return;
        MasterUid = _members.Keys.First();
        foreach (var m in _members.Values)
            effects.Add(new(m.Uid, PrivateRoomMessage.ChangeRoomMaster, MasterUid));
    }
}
