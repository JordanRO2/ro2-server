using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using RO2.Chats.Domain;
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
    public const ushort ResultJoined = CloseRangeChatRoom.ResultJoined;

    /// <summary>23538 result 10: string 9726 "Cannot find channel room."</summary>
    public const ushort ResultRoomNotFound = CloseRangeChatRoom.ResultRoomNotFound;

    /// <summary>23538 result 19: string 9736 "Please enter your password correctly."</summary>
    public const ushort ResultWrongPassword = CloseRangeChatRoom.ResultWrongPassword;

    /// <summary>23538 result 23: string 9516 "Already participating in the target channel."</summary>
    public const ushort ResultAlreadyMember = CloseRangeChatRoom.ResultAlreadyMember;

    /// <summary>23538 result 29: string 9758 "Cannot enter because the room is full."</summary>
    public const ushort ResultRoomFull = CloseRangeChatRoom.ResultRoomFull;

    /// <summary>Most rooms one character may be in at once (see <see cref="CloseRangeChatRoom.MaxRoomsPerCharacter"/>).</summary>
    public const int MaxRoomsPerCharacter = CloseRangeChatRoom.MaxRoomsPerCharacter;

    /// <summary>One queued send, built under the lock and delivered after it is released.</summary>
    public readonly record struct Send(ProudSession Session, ushort Opcode, byte[] Body);

    /// <summary>A master hand-off made because the owner's Chat connection closed (<see cref="OnSessionClosed"/>),
    /// for the exit signal to the Zone.</summary>
    public readonly record struct OwnerGoneHandOff(byte[] RoomGuid, uint OldMasterCharacterId, uint NewMasterCharacterId,
        int MemberCount);

    /// <summary>A Chat session as a room member: its messenger uid (23001), database id and name.</summary>
    public readonly record struct Member(ProudSession Session, uint CharacterId, ulong Uid, string Name);

    /// <summary>The room as the Zone published it (CloseRangeRoomSignals open signal).</summary>
    public readonly record struct OpenedRoom(byte[] Guid, byte Capacity, byte[] Password);

    /// <summary>A registered room: the id-only domain aggregate plus the uid -> connection map the domain does not know.</summary>
    private sealed class Entry(CloseRangeChatRoom room)
    {
        public readonly CloseRangeChatRoom Room = room;
        public readonly SortedDictionary<ulong, ProudSession> Sessions = new();
    }

    private static readonly Lock Gate = new();
    private static readonly Dictionary<Guid, Entry> Rooms = new();

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
            var created = new Entry(new CloseRangeChatRoom(room.Guid, room.Capacity, room.Password, ToDomain(owner)));
            created.Sessions[owner.Uid] = owner.Session;
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
            Entry? entry = null;
            if (roomGuid.Length == 16) Rooms.TryGetValue(new Guid(roomGuid), out entry);
            var (result, effects) = CloseRangeChatRoom.Join(entry?.Room, ToDomain(joiner), password, RoomsOfLocked(joiner.CharacterId));
            if (result == ResultJoined) entry!.Sessions[joiner.Uid] = joiner.Session;
            QueueLocked(entry, joiner.Session, effects, sends);
            return (result, result == ResultJoined ? entry!.Room.Members.Count : 0);
        }
    }

    /// <summary>
    /// 23022: the member whose database id is <paramref name="characterId"/> leaves room
    /// <paramref name="roomGuid"/>. Queues 23520 to the members who stay. When the leaver is the owner and members
    /// remain, the lowest remaining uid becomes the owner (<see cref="CloseRangeLeaveOutcome.HandedOff"/>).
    /// </summary>
    public static CloseRangeDeparture Leave(ReadOnlySpan<byte> roomGuid, uint characterId, List<Send> sends)
    {
        lock (Gate)
        {
            PruneDisconnectedLocked();
            if (roomGuid.Length != 16) return new CloseRangeDeparture(CloseRangeLeaveOutcome.UnknownRoom, 0, 0);
            var id = new Guid(roomGuid);
            if (!Rooms.TryGetValue(id, out var entry)) return new CloseRangeDeparture(CloseRangeLeaveOutcome.UnknownRoom, 0, 0);
            var (departure, effects) = entry.Room.Leave(characterId);
            QueueLocked(entry, null, effects, sends);
            SettleLocked(id, entry, departure);
            return departure;
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
            foreach (var (id, entry) in Rooms.ToList())
            {
                var found = entry.Sessions.Where(kv => ReferenceEquals(kv.Value, closing)).Select(kv => (ulong?)kv.Key).FirstOrDefault();
                if (found is not { } goneUid) continue;

                var goneCharacterId = entry.Room.Members[goneUid].CharacterId;
                var dead = entry.Sessions.Where(kv => !kv.Value.IsConnected).Select(kv => kv.Key).ToHashSet();
                var (departure, effects) = entry.Room.Close(goneUid, dead);
                QueueLocked(entry, null, effects, sends);
                SettleLocked(id, entry, departure);
                if (departure.Outcome == CloseRangeLeaveOutcome.HandedOff)
                    handOffs.Add(new OwnerGoneHandOff(entry.Room.GuidBytes, goneCharacterId, departure.NewMasterCharacterId,
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
            if (!Rooms.TryGetValue(new Guid(roomGuid), out var entry)) return Array.Empty<ProudSession>();
            return entry.Sessions.Values.Where(s => s.IsConnected).ToList();
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

    private static CloseRangeMember ToDomain(Member m) => new(m.Uid, m.CharacterId, m.Name);

    /// <summary>Maps the domain's ordered effects to sends. A null recipient is <paramref name="requester"/>.</summary>
    private static void QueueLocked(Entry? entry, ProudSession? requester, List<CloseRangeEffect> effects, List<Send> sends)
    {
        foreach (var e in effects)
        {
            var session = e.Recipient is { } uid ? entry!.Sessions[uid] : requester!;
            var room = entry?.Room;
            sends.Add(e.Message switch
            {
                CloseRangeMessage.JoinAnswer => new Send(session, ChatS2COpcodes.AnsJoinCloseRangeChatRoom, BuildJoinAnswer23538(e.Code)),
                CloseRangeMessage.MemberList => new Send(session, ChatS2COpcodes.AnsPrivateRoomInMember,
                    BuildMemberList23517(room!.GuidBytes, room.OwnerUid, room.Capacity, room.OwnerFirst())),
                CloseRangeMessage.JoinUser => new Send(session, ChatS2COpcodes.NfyPrivateChatRoomJoinUser,
                    BuildJoinUser23516(room!.GuidBytes, e.Uid, e.Text)),
                _ => new Send(session, ChatS2COpcodes.NfyChatRoomExitMmeberInfo, BuildExitMemberInfo23520(room!.GuidBytes, e.Uid)),
            });
        }
    }

    /// <summary>After a departure: forget the connections of members the room no longer lists, and drop the
    /// room when its owner left it empty.</summary>
    private static void SettleLocked(Guid id, Entry entry, CloseRangeDeparture departure)
    {
        SyncSessions(entry);
        if (departure.Outcome == CloseRangeLeaveOutcome.OwnerLeft) Rooms.Remove(id);
    }

    private static void SyncSessions(Entry entry)
    {
        foreach (var uid in entry.Sessions.Keys.Where(k => !entry.Room.Members.ContainsKey(k)).ToList())
            entry.Sessions.Remove(uid);
    }

    private static int RoomsOfLocked(uint characterId) => Rooms.Values.Count(e => e.Room.HasCharacter(characterId));

    private static void PruneDisconnectedLocked()
    {
        foreach (var (id, entry) in Rooms.ToList())
        {
            if (!entry.Sessions.TryGetValue(entry.Room.OwnerUid, out var owner))
            {
                Rooms.Remove(id);
                continue;
            }
            // A disconnected owner's room stays: its Disconnected event (OnSessionClosed) owns the hand-off and the
            // OwnerGone signal, and removing the room here would make that event find nothing.
            // shortcut: if the owner socket died in the sub-ms gap before OpenFromZoneAsync subscribed Disconnected,
            // that event never fires and this lazy prune now skips the ghost owner forever (it accepts joins). Upgrade
            // to a periodic sweep that evicts an owner disconnected longer than a grace window if that race is ever seen.
            if (!owner.IsConnected) continue;
            entry.Room.Drop(entry.Sessions.Where(kv => !kv.Value.IsConnected).Select(kv => kv.Key).ToList());
            SyncSessions(entry);
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
