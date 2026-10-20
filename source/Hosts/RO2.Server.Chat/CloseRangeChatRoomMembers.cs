using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;

namespace RO2.Server.Chat;

/// <summary>
/// Who is in each close-range ("local") chat room, on the Chat server: 23020 ReqJoinCloseRangeChatRoom joins
/// one, 23022 ReqExitCloseRangeChatRoom leaves it. In memory, like <see cref="PrivateChatRooms"/>.
/// </summary>
/// <remarks>
/// <para>
/// The Zone opens the room (1533 -> 2762 + the 2763 sign) and publishes it (owner, capacity, password) on the
/// signal channel; the Chat server learns it from there on the first join
/// (ReqJoinCloseRangeHandler, CloseRangeRoomSignals.FindOpenRoomAsync) and keeps its member list here, the
/// owner first.
/// </para>
/// <para>
/// A join produces three sends:
/// <list type="bullet">
/// <item>23538 AnsJoinCloseRangeChatRoom <c>[u16 result]</c>, to the joiner. Result 0 makes the client treat the
///   room as its current one and open the room window; any other code shows a refusal message.</item>
/// <item>23517 AnsPrivateRoomInMember, to the joiner: <c>[u16 0][u32 len-6][u64 masterUid][guid16][u32
///   capacity][u16 n]{u64 uid, name8}</c> with master = the owner, capacity 10 (the 1533 flag) and n = 2, the
///   owner then the joiner. Each member is listed under its messenger uid (the uid its own 23001 carried, see
///   <see cref="MessengerUid"/>) and its character name. The same shape as a private room's
///   (PrivateChatRooms), which the client uses to fill its member map. <see cref="BuildMemberList23517"/>
///   builds it.</item>
/// <item>23516 NfyPrivateChatRoomJoinUser <c>[guid16][u32 len-20][u64 joinerUid][name8]</c>, to every member,
///   the joiner included (<see cref="BuildJoinUser23516"/>).</item>
/// </list>
/// A member who leaves is announced to those who stay with 23520 NfyChatRoomExitMmeberInfo
/// <c>[guid16][u64 uid]</c>. The client finds the GUID is its current room, removes that uid from its member
/// map and prints the leave message.
/// </para>
/// <para>
/// Refusals use the messenger result codes that name the case: 10 "Cannot find channel room." (9726),
/// 19 "Please enter your password correctly." (9736), 23 "Already participating in the target channel."
/// (9516), 29 "Cannot enter because the room is full." (9758).
/// </para>
/// <para>
/// The owner leaving a room others are still in is a MASTER HAND-OFF and the room stays open: the room's sign,
/// with the same GUID, moves to another member (the Zone's 2766 + 2770 [new master, 10, 1]). The new master is
/// the lowest remaining uid, the rule of the private rooms (PrivateChatRooms.AfterDepartureLocked). The members
/// who stay get the 23520 sent for any member who leaves, naming the old owner. No 23533
/// NfyPrivateChatRoom_ChangeRoomMaster is sent for a close-range room. The Zone learns of the hand-off from the
/// exit signal's new master (ReqExitCloseRangeHandler, CloseRangeRoomSignals.PublishExitAsync).
/// </para>
/// <para>
/// The same hand-off happens when the owner's Chat connection closes (<see cref="OnSessionClosed"/>, called from
/// the Disconnected event ReqJoinCloseRangeHandler subscribes for every member): the Zone sends 2766 + 2770
/// [new master, 10, 1] and no 2765, and the members are told with the same 23520. A member other than the owner
/// whose connection closes is dropped from the list without a notice, and an owner who leaves an empty room
/// closes it. A room whose owner's connection is found gone by a later join or exit before its Disconnected
/// event ran is dropped (<c>PruneDisconnectedLocked</c>; the next join reads the Zone's open signal again).
/// </para>
/// </remarks>
public static class CloseRangeChatRoomMembers
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

    /// <summary>One queued send, built under the lock and delivered after it is released.</summary>
    public readonly record struct Send(ProudSession Session, ushort Opcode, byte[] Body);

    /// <summary>What <see cref="Leave"/> did.</summary>
    public enum LeaveOutcome
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

    /// <summary>What <see cref="Leave"/> did: the outcome, the member count left (owner included; 0 unless
    /// <see cref="LeaveOutcome.MemberLeft"/> or <see cref="LeaveOutcome.HandedOff"/>) and, for a hand-off, the new
    /// owner's database id (0 otherwise).</summary>
    public readonly record struct Departure(LeaveOutcome Outcome, int MemberCount, uint NewMasterCharacterId);

    /// <summary>A master hand-off made because the owner's Chat connection closed (<see cref="OnSessionClosed"/>),
    /// for the exit signal to the Zone.</summary>
    public readonly record struct OwnerGoneHandOff(byte[] RoomGuid, uint OldMasterCharacterId, uint NewMasterCharacterId,
        int MemberCount);

    /// <summary>A Chat session as a room member: its messenger uid (23001), database id and name.</summary>
    public readonly record struct Member(ProudSession Session, uint CharacterId, ulong Uid, string Name);

    /// <summary>The room as the Zone published it (CloseRangeRoomSignals open signal).</summary>
    public readonly record struct OpenedRoom(byte[] Guid, byte Capacity, byte[] Password);

    private sealed class Room
    {
        public required byte[] GuidBytes;
        public required ulong OwnerUid; // the opener, or the member a hand-off made owner since
        public required byte Capacity;
        public required byte[] Password;
        // Keyed and ordered by uid, as PrivateChatRooms lists everyone after the master.
        public readonly SortedDictionary<ulong, Member> Members = new();
    }

    private static readonly Lock Gate = new();
    private static readonly Dictionary<Guid, Room> Rooms = new();

    /// <summary>Whether this process holds a member list for room <paramref name="roomGuid"/>, with its owner
    /// still connected.</summary>
    public static bool IsOpen(ReadOnlySpan<byte> roomGuid)
    {
        if (roomGuid.Length != 16) return false;
        lock (Gate)
        {
            PruneDisconnectedLocked();
            return Rooms.ContainsKey(new Guid(roomGuid));
        }
    }

    /// <summary>
    /// Starts the member list of a room the Zone published, with <paramref name="owner"/> as its first
    /// member. Nothing changes when a list for it already exists (another join got there first).
    /// </summary>
    public static void Open(OpenedRoom room, Member owner)
    {
        if (room.Guid.Length != 16) return;
        lock (Gate)
        {
            var id = new Guid(room.Guid);
            if (Rooms.ContainsKey(id)) return;
            var created = new Room
            {
                GuidBytes = room.Guid,
                OwnerUid = owner.Uid,
                Capacity = room.Capacity,
                Password = room.Password,
            };
            created.Members[owner.Uid] = owner;
            Rooms[id] = created;
        }
    }

    /// <summary>
    /// 23020: <paramref name="joiner"/> joins room <paramref name="roomGuid"/> with <paramref name="password"/>
    /// (UTF-16LE as sent). Queues the 23538 answer and, on success, 23517 to the joiner and 23516 to every
    /// member. Returns the 23538 result and, on success, the member count after the join.
    /// </summary>
    public static (ushort Result, int MemberCount) Join(ReadOnlySpan<byte> roomGuid, Member joiner,
        ReadOnlySpan<byte> password, List<Send> sends)
    {
        lock (Gate)
        {
            PruneDisconnectedLocked();
            ushort result;
            Room? room = null;
            if (roomGuid.Length != 16 || !Rooms.TryGetValue(new Guid(roomGuid), out room))
                result = ResultRoomNotFound;
            else if (room.Members.ContainsKey(joiner.Uid))
                result = ResultAlreadyMember;
            else if (RoomsOfLocked(joiner.CharacterId) >= MaxRoomsPerCharacter)
                result = ResultAlreadyMember;
            else if (!password.SequenceEqual(room.Password))
                result = ResultWrongPassword;
            else if (room.Members.Count >= room.Capacity)
                result = ResultRoomFull;
            else
                result = ResultJoined;

            sends.Add(new Send(joiner.Session, ChatS2COpcodes.AnsJoinCloseRangeChatRoom, BuildJoinAnswer23538(result)));
            if (result != ResultJoined || room is null) return (result, 0);

            room.Members[joiner.Uid] = joiner;
            sends.Add(new Send(joiner.Session, ChatS2COpcodes.AnsPrivateRoomInMember,
                BuildMemberList23517(room.GuidBytes, room.OwnerUid, room.Capacity, OwnerFirst(room))));
            var joinUser = BuildJoinUser23516(room.GuidBytes, joiner.Uid, joiner.Name);
            foreach (var m in room.Members.Values)
                sends.Add(new Send(m.Session, ChatS2COpcodes.NfyPrivateChatRoomJoinUser, joinUser));
            return (result, room.Members.Count);
        }
    }

    /// <summary>
    /// 23022: the member whose database id is <paramref name="characterId"/> leaves room
    /// <paramref name="roomGuid"/>. Queues 23520 to the members who stay. When the leaver is the owner and members
    /// remain, the lowest remaining uid becomes the owner (<see cref="LeaveOutcome.HandedOff"/>).
    /// </summary>
    public static Departure Leave(ReadOnlySpan<byte> roomGuid, uint characterId, List<Send> sends)
    {
        lock (Gate)
        {
            PruneDisconnectedLocked();
            if (roomGuid.Length != 16) return new Departure(LeaveOutcome.UnknownRoom, 0, 0);
            var id = new Guid(roomGuid);
            if (!Rooms.TryGetValue(id, out var room)) return new Departure(LeaveOutcome.UnknownRoom, 0, 0);
            Member? leaver = null;
            foreach (var m in room.Members.Values)
                if (m.CharacterId == characterId) { leaver = m; break; }
            if (leaver is not { } gone) return new Departure(LeaveOutcome.NotAMember, 0, 0);
            return DepartLocked(id, room, gone, sends);
        }
    }

    /// <summary>
    /// A Chat connection is closing: takes <paramref name="closing"/> out of every room it is in, at once rather
    /// than at the next join or exit. Where it was the owner and others remain, hands the room to the lowest
    /// remaining uid and queues the 23520 naming it to those who stay; returns those hand-offs, which the caller
    /// signals to the Zone (OwnerGone). Anywhere else it is dropped without a notice, and a room it owned alone
    /// closes. During the Disconnected event the session is not disposed yet, so it is named explicitly, as
    /// PrivateChatRooms.OnSessionClosedAsync does.
    /// </summary>
    public static List<OwnerGoneHandOff> OnSessionClosed(ProudSession closing, List<Send> sends)
    {
        var handOffs = new List<OwnerGoneHandOff>();
        lock (Gate)
        {
            foreach (var (id, room) in Rooms.ToList())
            {
                Member? found = null;
                foreach (var m in room.Members.Values)
                    if (ReferenceEquals(m.Session, closing)) { found = m; break; }
                if (found is not { } gone) continue;

                // Other members already gone first, so neither the notice nor the ownership goes to a dead one.
                foreach (var dead in room.Members.Values
                             .Where(m => m.Uid != gone.Uid && m.Uid != room.OwnerUid && !m.Session.IsConnected).ToList())
                    room.Members.Remove(dead.Uid);

                if (gone.Uid != room.OwnerUid)
                {
                    room.Members.Remove(gone.Uid);
                    continue;
                }
                var departure = DepartLocked(id, room, gone, sends);
                if (departure.Outcome == LeaveOutcome.HandedOff)
                    handOffs.Add(new OwnerGoneHandOff(room.GuidBytes, gone.CharacterId, departure.NewMasterCharacterId,
                        departure.MemberCount));
            }
        }
        return handOffs;
    }

    /// <summary>
    /// The connected sessions of a room's members, empty when this process holds no member list for
    /// <paramref name="roomGuid"/>. Read-only; for relaying a line typed in the room (23023 -> 23529) to
    /// everyone in it.
    /// </summary>
    public static IReadOnlyList<ProudSession> MemberSessions(ReadOnlySpan<byte> roomGuid)
    {
        if (roomGuid.Length != 16) return Array.Empty<ProudSession>();
        lock (Gate)
        {
            if (!Rooms.TryGetValue(new Guid(roomGuid), out var room)) return Array.Empty<ProudSession>();
            return room.Members.Values.Select(m => m.Session).Where(s => s.IsConnected).ToList();
        }
    }

    /// <summary>Delivers queued sends in order, skipping connections that have gone away.</summary>
    public static async Task DeliverAsync(List<Send> sends)
    {
        foreach (var s in sends)
        {
            if (!s.Session.IsConnected) continue;
            try
            {
                await s.Session.SendInternalRmiAsync(s.Opcode, s.Body);
            }
            catch (Exception ex) when (ex is ObjectDisposedException or SocketException)
            {
                // Its connection went between the check and the send; it is pruned at the next join or exit,
                // and one dead member must not cancel the sends to the others.
            }
        }
    }

    // ---------------------------------------------------------------- state (callers hold Gate)

    /// <summary>
    /// <paramref name="gone"/> leaves <paramref name="room"/>: 23520 to every member who stays, then, when it was the
    /// owner, the room closes if nobody is left or passes to the lowest remaining uid (the room keeps its GUID,
    /// capacity and password).
    /// </summary>
    private static Departure DepartLocked(Guid id, Room room, Member gone, List<Send> sends)
    {
        room.Members.Remove(gone.Uid);
        var notice = BuildExitMemberInfo23520(room.GuidBytes, gone.Uid);
        foreach (var m in room.Members.Values)
            sends.Add(new Send(m.Session, ChatS2COpcodes.NfyChatRoomExitMmeberInfo, notice));

        if (gone.Uid != room.OwnerUid)
            return new Departure(LeaveOutcome.MemberLeft, room.Members.Count, 0);
        if (room.Members.Count == 0)
        {
            Rooms.Remove(id);
            return new Departure(LeaveOutcome.OwnerLeft, 0, 0);
        }
        room.OwnerUid = room.Members.Keys.First(); // lowest remaining uid (SortedDictionary)
        return new Departure(LeaveOutcome.HandedOff, room.Members.Count, room.Members[room.OwnerUid].CharacterId);
    }

    private static int RoomsOfLocked(uint characterId)
    {
        int n = 0;
        foreach (var room in Rooms.Values)
            foreach (var m in room.Members.Values)
                if (m.CharacterId == characterId) { n++; break; }
        return n;
    }

    private static void PruneDisconnectedLocked()
    {
        foreach (var (id, room) in Rooms.ToList())
        {
            if (!room.Members.TryGetValue(room.OwnerUid, out var owner) || !owner.Session.IsConnected)
            {
                Rooms.Remove(id);
                continue;
            }
            foreach (var gone in room.Members.Values.Where(m => !m.Session.IsConnected).ToList())
                room.Members.Remove(gone.Uid);
        }
    }

    // ---------------------------------------------------------------- bodies

    /// <summary>23538 AnsJoinCloseRangeChatRoom: <c>[u16 result]</c>.</summary>
    public static byte[] BuildJoinAnswer23538(ushort result)
    {
        var body = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(body, result);
        return body;
    }

    /// <summary>The members as 23517 lists them: the owner first, then the others in uid order.</summary>
    private static List<(ulong Uid, string Name)> OwnerFirst(Room room)
    {
        var list = new List<(ulong Uid, string Name)>(room.Members.Count);
        if (room.Members.TryGetValue(room.OwnerUid, out var owner)) list.Add((owner.Uid, owner.Name));
        foreach (var m in room.Members.Values)
            if (m.Uid != room.OwnerUid) list.Add((m.Uid, m.Name));
        return list;
    }

    /// <summary>
    /// 23517 AnsPrivateRoomInMember for a close-range room: <c>[u16 0][u32 len-6][u64 masterUid][guid16][u32
    /// capacity][u16 n]{u64 uid, u8 nChars, UTF-16LE name}</c>, <paramref name="members"/> in the order given
    /// (the owner first).
    /// </summary>
    public static byte[] BuildMemberList23517(byte[] roomGuid, ulong masterUid, uint capacity,
        IReadOnlyList<(ulong Uid, string Name)> members)
    {
        using var w = new PacketWriter();
        w.WriteUInt16(0);
        var lenPos = w.Reserve(4);
        int start = w.Length;
        w.WriteUInt64(masterUid);
        w.WriteBytes(roomGuid);
        w.WriteUInt32(capacity);
        w.WriteUInt16((ushort)members.Count);
        foreach (var (uid, name) in members)
        {
            w.WriteUInt64(uid);
            WriteName8(w, name);
        }
        w.WriteAt(lenPos, (uint)(w.Length - start));
        return w.ToArray();
    }

    /// <summary>23516 NfyPrivateChatRoomJoinUser: <c>[guid16][u32 len-20][u64 uid][u8 nChars][UTF-16LE name]</c>.</summary>
    public static byte[] BuildJoinUser23516(byte[] guid, ulong uid, string name)
    {
        using var w = new PacketWriter();
        w.WriteBytes(guid);
        var lenPos = w.Reserve(4);
        int start = w.Length;
        w.WriteUInt64(uid);
        WriteName8(w, name);
        w.WriteAt(lenPos, (uint)(w.Length - start));
        return w.ToArray();
    }

    /// <summary>23520 NfyChatRoomExitMmeberInfo: <c>[guid16][u64 uid]</c>.</summary>
    public static byte[] BuildExitMemberInfo23520(byte[] guid, ulong uid)
    {
        var body = new byte[24];
        guid.CopyTo(body, 0);
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(16), uid);
        return body;
    }

    private static void WriteName8(PacketWriter w, string s)
    {
        var bytes = Encoding.Unicode.GetBytes(s);
        int chars = Math.Min(bytes.Length / 2, byte.MaxValue);
        w.WriteByte((byte)chars);
        w.WriteBytes(bytes.AsSpan(0, chars * 2));
    }
}
