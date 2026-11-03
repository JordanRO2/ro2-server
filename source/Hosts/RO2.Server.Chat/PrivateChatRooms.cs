using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using RO2.Chats.Domain;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;

namespace RO2.Server.Chat;

/// <summary>
/// User-created ("private") messenger chat rooms: 23011 join-or-create, 23017 member list, 23013 leave.
/// In memory, like retail's rooms: they live only while someone is in them.
///
/// Every layout below is the wire layout of its opcode.
/// Names are [u8 nChars][UTF-16LE], uids are the 8-byte messenger uids, i.e. each member's full 23001
/// uid (<see cref="MessengerUid"/>), which is its character GID:
/// 23515 AnsPrivateChatRoomJoin [u16 0][u32 len-6][guid16][u64 masterUid][name8 room][name8 password]
/// 23516 NfyPrivateChatRoomJoinUser [guid16][u32 len-20][u64 uid][name8]
/// 23517 AnsPrivateRoomInMember [u16 0][u32 len-6][u64 masterUid][guid16][u32 100][u16 n]{u64 uid, name8}
/// 23519 AnsChatRoomExit [u16 0][guid16]
/// 23520 NfyChatRoomExitMemberInfo [guid16][u64 uid]
/// 23533 NfyPrivateChatRoom_ChangeRoomMaster [u64 newMasterUid][guid16]
/// 23504 NfyLogoutPlayer_PrivateChatRoom [u64 uid][guid16]
///
/// Behaviour:
/// - A 23011 for a name nobody holds creates the room with the sender as master (23515 masterUid ==
/// joiner, 23517 count 1). Otherwise the sender joins it. Rooms are found by name and keep one GUID
/// across joins. A re-join is NOT the same messenger login asking again: it follows a fresh 23001, i.e.
/// the old Chat connection had already gone and taken the member with it.
/// - The answer is three packets in one tick: 23515 and 23517 to the joiner, 23516 to every member
/// including the joiner (the room's other members receive the same 23516 in that ms).
/// - 23517 lists the master first, then everyone else in ascending uid order, and its u32 is 100
/// (the capacity).
/// - When the master goes, the lowest remaining uid becomes master (even if it had joined last). 23533
/// follows the departure notice in the same tick.
/// - A member whose connection is gone is announced to the rest with 23504.
///
/// A REFUSED JOIN is answered with 23515 alone (no 23516 / 23517), a zeroed record under a non-zero result:
/// 23515 refusal [u16 code][u32 26][guid16 0][u64 master 0][u8 0 room][u8 0 password] 32 bytes
/// (<see cref="BuildJoinRefusal"/>), exactly 17 00 1a 00 00 00 + 26 zero bytes for code 23 (9516 "Already
/// participating in the target channel."), a record whose u32 length (26) is the empty record's. It is the
/// answer to a 23011 of a uid the server still lists as a member of that room (an auto-rejoin of a room the
/// uid joined in an earlier session and never left with 23013). So the rule is "the joiner's uid is already in
/// the room's member list -> 23", checked first (<see cref="ResultAlreadyInRoom"/>). After
/// PruneDisconnectedLocked every listed member holds a live session, so a relog whose old Chat connection is
/// not reaped yet gets 23 for its auto-rejoins until that connection drops, as retail's did.
/// A wrong password and a full room get the same record with the code the client's strings name: 26 (9735
/// "Private channel. Please enter your password.") when the joiner sent no password, 19 (9736 "Please enter your
/// password correctly.") when it sent a wrong one, 29 (9758 "Cannot enter because the room is full."), the
/// result-code arms of those strings, which the 23515 consumer calls for any non-zero result. THOSE THREE
/// HAVE NO RETAIL FRAME, so the code -> case choice is ours, from the strings, as
/// CloseRangeChatRoomMembers does for 23538.
///
/// The master's operations: 23014 password, 23015 kick, 23016 invite. NONE OF THESE HAS A RETAIL FRAME
/// except one 23532 (see BuildInviteRoomInfo), which matches the reader's layout and carries 0 where
/// 23517 carries 100.
/// Every other layout below is the CLIENT'S READER alone (the client, build 303), and each is filled from room state:
/// 23521 AnsPrivateChatRoomChangePass [u16 result][u32 len][guid16][u8 n][n x UTF-16LE password]
/// (u8 n clamped to 4, 2n bytes); the same reader the server side uses for the 23014 request.
/// 23522 AnsPrivateChatRoomInUserKick [u16 result]
/// 23523 NfyPrivateChatRoomKickUser [guid16][u64 uid]
/// 23524 NfyPrivateChatRoomMessage_ToKickUser [guid16]
/// 23525 AnsPrivateChatRoomInvite [u16 result]
/// 23526 NfyPrivateChatRoomInvate_InRoomMember = the 23516 body: both share one reader, so this is the
/// 23516 shape under another opcode.
/// 23532 NfyInviteRoomInfo [u32 len][u8 n (&lt;=50)][room name][23517 record]: the reader takes the name, then
/// the same record reader 23517 uses after its [u16].
/// 23527 NfyPrivateChatRoomInvate_JoinMember [u32 len][guid16][u64 master][u8 n&lt;=50][name]
/// is deliberately NOT sent: it and 23532 both open the room's tab, see Invite.
/// Every member name (23516, 23526 and each 23517 / 23532 entry) is read by a reader that keeps at
/// most 20 chars (<see cref="MemberNameMaxChars"/>).
/// What the client does with each:
/// 23521 -> result != 0 shows the code, 0 prints 9546
/// "Set Password: [@1. @2, @3]" with the echoed password. Nothing else changes client-side.
/// 23522/23525 -> only a nonzero result is shown.
/// 23523 -> prints 9531 "Ban Channel" with the member's name and drops it from the room.
/// 23524 -> prints 9531 with the local name, closes the tab.
/// 23526 -> adds the member, prints 9523 "Channel Invite".
/// 23532 -> registers the room, adds every listed member, opens its tab, prints 9519.
/// The refusal codes are arms of the client's messenger result-code switch, chosen by the string each arm
/// shows (data/strings/string_ui.json); see the Result* constants.
/// </summary>
public static class PrivateChatRooms
{
 /// <summary>23517's u32 capacity field, 100.</summary>
    public const int MaxMembers = PrivateRoom.MaxMembers;

 /// <summary>
 /// Private rooms one client can hold at once: the client's room manager sizes its room-slot
 /// bitset with 10 entries, and registering a room returns null once all 10 are taken. The 23532 consumer
 /// does not test that null, so an invite
 /// must never push an 11th room on a client.
 /// </summary>
    public const int ClientRoomSlots = PrivateRoom.ClientRoomSlots;

 /// <summary>The longest room name the 23532 reader keeps: it clamps nChars at 50.</summary>
    public const int InviteRoomNameMaxChars = PrivateRoom.InviteRoomNameMaxChars;

 /// <summary>
 /// The longest member name the client's member reader keeps ([u64 uid][u8 n, clamped at 20]
 /// [2n bytes]). It reads the one member of 23516 and 23526 and
 /// every entry of the 23517 / 23532 list. In a list a
 /// longer name would push every following entry out of phase, so the builders cut at the same place.
 /// Our own character names stop at 16 (CharacterRepository), so today this never cuts one.
 /// </summary>
    public const int MemberNameMaxChars = PrivateRoom.MemberNameMaxChars;

 /// <summary>23014's password: the client's dialog sends at most 4 wchar and
 /// the reader clamps n at 4; string 9551 says the 4 are digits.</summary>
    public const int PasswordMaxChars = PrivateRoom.PasswordMaxChars;

    // The result codes live in RO2.Chats.Domain.PrivateRoom (each one's client string is documented there).
    public const ushort ResultOk = PrivateRoom.ResultOk;
    public const ushort ResultTargetMissing = PrivateRoom.ResultTargetMissing;
    public const ushort ResultRoomLimit = PrivateRoom.ResultRoomLimit;
    public const ushort ResultNoRoom = PrivateRoom.ResultNoRoom;
    public const ushort ResultBadPassword = PrivateRoom.ResultBadPassword;
    public const ushort ResultNotMaster = PrivateRoom.ResultNotMaster;
    public const ushort ResultNotMember = PrivateRoom.ResultNotMember;
    public const ushort ResultNoSuchCharacter = PrivateRoom.ResultNoSuchCharacter;
    public const ushort ResultWrongPassword = PrivateRoom.ResultWrongPassword;
    public const ushort ResultAlreadyInRoom = PrivateRoom.ResultAlreadyInRoom;
    public const ushort ResultPasswordRequired = PrivateRoom.ResultPasswordRequired;
    public const ushort ResultRoomFull = PrivateRoom.ResultRoomFull;

 /// <summary>An online messenger character a 23016 names: what <see cref="Join"/> stores per member.</summary>
    public readonly record struct Invitee(ProudSession Session, uint CharacterId, ulong Uid, string Name);

 /// <summary>One queued send, built under the lock and delivered after it is released.</summary>
    public readonly record struct Send(ProudSession Session, ushort Opcode, byte[] Body);

 /// <summary>A registered room: the id-only domain aggregate plus the uid -> connection map the domain does not know.</summary>
    private sealed class Entry(PrivateRoom room)
    {
        public readonly PrivateRoom Room = room;
        public readonly SortedDictionary<ulong, ProudSession> Sessions = new();
    }

    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, Entry> ByName = new(StringComparer.Ordinal);
    private static readonly Dictionary<Guid, Entry> ById = new();

 /// <summary>
 /// 23011: join the room called <paramref name="roomName"/>, creating it if nobody holds it.
 /// <paramref name="uid"/> is the joiner's full <see cref="MessengerUid"/>, the value every body lists
 /// it under; <paramref name="characterId"/> is its session.CharacterId, used only to find it again on 23013.
 /// A refusal queues the lone 23515 of <see cref="BuildJoinRefusal"/> to <paramref name="session"/>, checked in
 /// this order: the uid is already a member (23, the retail case), then the password (26 empty / 19 wrong),
 /// then a full room (29); see the class remarks.
 /// </summary>
    public static JoinOutcome Join(ProudSession session, uint characterId, ulong uid, string memberName, string roomName, string password, List<Send> sends)
    {
        lock (Gate)
        {
            PruneDisconnectedLocked(sends, closing: null);
            if (!ByName.TryGetValue(roomName, out var entry))
            {
                var id = Guid.NewGuid();
                entry = new Entry(new PrivateRoom(id, roomName, password, uid));
                ByName[roomName] = entry;
                ById[id] = entry;
            }

            var (outcome, effects) = entry.Room.Join(uid, characterId, memberName, password);
            if (outcome == JoinOutcome.Joined) entry.Sessions[uid] = session;
            QueueLocked(entry, session, effects, default, sends);
            return outcome;
        }
    }

 /// <summary>23017: the member list for <paramref name="requester"/>. False if no such room exists.</summary>
    public static bool MemberList(ReadOnlySpan<byte> roomGuid, ProudSession requester, List<Send> sends)
    {
        lock (Gate)
        {
            PruneDisconnectedLocked(sends, closing: null);
            if (!ById.TryGetValue(new Guid(roomGuid), out var entry)) return false;
            QueueLocked(entry, requester, entry.Room.MemberList(), default, sends);
            return true;
        }
    }

 /// <summary>
 /// 23013: the member whose session.CharacterId is <paramref name="characterId"/> leaves. The remaining
 /// members get 23520 under the leaver's wire uid, plus 23533 if the master left. The 23519 answer to the
 /// leaver is the caller's (it is sent whether or not it was a member).
 /// Takes the CharacterId, not the wire uid, because that is what the caller (ReqChatRoomExitHandler)
 /// holds; the wire uid is the full GID and would never equal it.
 /// </summary>
    public static bool Leave(ReadOnlySpan<byte> roomGuid, uint characterId, List<Send> sends)
    {
        lock (Gate)
        {
            PruneDisconnectedLocked(sends, closing: null);
            if (!ById.TryGetValue(new Guid(roomGuid), out var entry)) return false;
            var (left, effects) = entry.Room.Leave(characterId);
            if (!left) return false;
            QueueLocked(entry, null, effects, default, sends);
            SettleLocked(entry);
            return true;
        }
    }

 /// <summary>
 /// The connected sessions of a room's members, empty if <paramref name="roomGuid"/> is not a private
 /// room. Read-only. For relaying room chat (23018 with a private room's guid → 23529 to its members).
 /// </summary>
    public static IReadOnlyList<ProudSession> MemberSessions(ReadOnlySpan<byte> roomGuid)
    {
        lock (Gate)
        {
            if (!ById.TryGetValue(new Guid(roomGuid), out var entry)) return Array.Empty<ProudSession>();
            return entry.Sessions.Values.Where(s => s.IsConnected).ToList();
        }
    }

 /// <summary>
 /// 23016: the master <paramref name="inviterUid"/> invites <paramref name="invitee"/> (null when no
 /// online messenger character has the name). Queues 23525 [u16 result] to <paramref name="inviter"/>
 /// in every case. On success the invitee becomes a member, the members already in get 23526 (the
 /// 23516 body naming the invitee, so each client adds it and prints 9523), and the invitee gets 23532
 /// with the room and its member list, which opens the room's tab (9519).
 ///
 /// The invitee gets 23532 and NOT 23527 NfyPrivateChatRoomInvate_JoinMember, because the two exclude
 /// each other. 23527 and 23532 both register the room with the client's room-group add, which takes a fresh
 /// client room slot on every call. Neither looks the guid up first, unlike
 /// the 23515 consumer, which finds the channel and only reprints 9519 for a room it holds.
 /// The room-group add's only other caller is the 23514 list rebuild, so sending both would give the
 /// invitee two tabs for one room and use two of its 10 slots. 23532 is the one that also fills the member
 /// list (flagging the master) and selects the tab. 23527
 /// ([u32 len][guid16][u64 master][u8 n&lt;=50][name]) would leave the list
 /// empty until the tab is clicked (the tab's join handler asks with 23017). Neither has a
 /// known retail frame in this role, so which one retail sent is unknown. 23532 is declared after
 /// 23528-23531 and carries the full list, which reads like 23527's replacement; that is an inference,
 /// not evidence. The one thing 23527 does better, testing the null an 11th room gets, is covered here
 /// by the <see cref="ClientRoomSlots"/> check.
 /// A name nobody online holds and a character who is offline both get 18 (9574): no messenger string
 /// says "offline", and 18 is the only code retail uses for a name it cannot serve (23508).
 /// </summary>
    public static ushort Invite(ReadOnlySpan<byte> roomGuid, ProudSession inviter, ulong inviterUid, Invitee? invitee, List<Send> sends)
    {
        lock (Gate)
        {
            PruneDisconnectedLocked(sends, closing: null);
            ById.TryGetValue(new Guid(roomGuid), out var entry);
            var online = invitee is { } i && i.Session.IsConnected ? invitee : null;
            PrivateRoomMember? who = online is { } o ? new PrivateRoomMember(o.Uid, o.CharacterId, o.Name) : null;
            var (result, effects) = PrivateRoom.Invite(entry?.Room, inviterUid, who, who is { } w ? RoomsHeldLocked(w.Uid) : 0);
            if (result == ResultOk) entry!.Sessions[online!.Value.Uid] = online.Value.Session;
            QueueLocked(entry, inviter, effects, default, sends);
            return result;
        }
    }

 /// <summary>
 /// 23015: the master <paramref name="kickerUid"/> expels member <paramref name="targetUid"/>. Queues
 /// 23522 [u16 result] to <paramref name="kicker"/> in every case. On success the target leaves the
 /// room, gets 23524 [guid] (its client prints 9531 and closes the tab), and every remaining member,
 /// the kicker included, gets 23523 [guid][u64 uid] (each client drops the member and prints 9531).
 /// </summary>
    public static ushort Kick(ReadOnlySpan<byte> roomGuid, ProudSession kicker, ulong kickerUid, ulong targetUid, List<Send> sends)
    {
        lock (Gate)
        {
            PruneDisconnectedLocked(sends, closing: null);
            ById.TryGetValue(new Guid(roomGuid), out var entry);
            var (result, effects) = PrivateRoom.Kick(entry?.Room, kickerUid, targetUid);
            QueueLocked(entry, kicker, effects, default, sends);
            if (entry is not null) SettleLocked(entry);
            return result;
        }
    }

 /// <summary>
 /// 23014: the master <paramref name="uid"/> sets the room password, which the next 23011 into the room
 /// is checked against (<see cref="Join"/>) and 23515 then carries. Queues 23521 to
 /// <paramref name="requester"/> only: no opcode tells the other members, and the client keeps no copy
 /// of the password for its join handler to update. A refusal keeps 23521's shape, with the
 /// request's guid and an empty password, so the reader's VarBlob is whole.
 /// </summary>
    public static ushort ChangePassword(ReadOnlySpan<byte> roomGuid, ProudSession requester, ulong uid, string password, List<Send> sends)
    {
        lock (Gate)
        {
            PruneDisconnectedLocked(sends, closing: null);
            ById.TryGetValue(new Guid(roomGuid), out var entry);
            var (result, effects) = PrivateRoom.ChangePassword(entry?.Room, uid, password);
            QueueLocked(entry, requester, effects, roomGuid, sends);
            return result;
        }
    }

 /// <summary>
 /// Remove a closing Chat session from every room at once and announce it (23504, plus 23533 when it
 /// was the master). Without this call the same happens lazily: every Join/Leave/MemberList/Invite/Kick/
 /// ChangePassword first drops members whose session has been disposed. During
 /// ProudServer.SessionDisconnected the session is not disposed yet, so it is named explicitly here.
 /// </summary>
    public static async Task OnSessionClosedAsync(ProudSession closing)
    {
        var sends = new List<Send>();
        lock (Gate) PruneDisconnectedLocked(sends, closing);
        await DeliverAsync(sends);
    }

 /// <summary>Deliver queued sends in order, skipping connections that have gone away.</summary>
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




            }
        }
    }


    private static void PruneDisconnectedLocked(List<Send> sends, ProudSession? closing)
    {
        foreach (var entry in ById.Values.ToList())
        {
            var gone = entry.Sessions
                .Where(kv => !kv.Value.IsConnected || ReferenceEquals(kv.Value, closing))
                .Select(kv => kv.Key).ToHashSet();
            if (gone.Count == 0) continue;
            QueueLocked(entry, null, entry.Room.Depart(gone), default, sends);
            SettleLocked(entry);
        }
    }

 /// <summary>After a departure: forget the sessions of members the room dropped, and drop an emptied room from the registry.</summary>
    private static void SettleLocked(Entry entry)
    {
        foreach (var uid in entry.Sessions.Keys.Where(k => !entry.Room.Members.ContainsKey(k)).ToList())
            entry.Sessions.Remove(uid);
        if (!entry.Room.IsEmpty) return;
        ByName.Remove(entry.Room.Name);
        ById.Remove(entry.Room.Id);
    }

 /// <summary>
 /// Turn the domain's ordered effects into sends: each recipient uid resolves to its connection (null recipient =
 /// <paramref name="requester"/>), each message to its opcode and wire bytes. <paramref name="requestGuid"/> is
 /// the guid the request carried, which 23521 echoes even when no room matched it.
 /// </summary>
    private static void QueueLocked(Entry? entry, ProudSession? requester, List<PrivateRoomEffect> effects, ReadOnlySpan<byte> requestGuid, List<Send> sends)
    {
        var room = entry?.Room;
        foreach (var e in effects)
        {
            var session = e.Recipient is { } r ? entry!.Sessions[r] : requester!;
            var (opcode, body) = e.Message switch
            {
                PrivateRoomMessage.JoinRefusal => (ChatS2COpcodes.AnsPrivateChatRoomJoin, BuildJoinRefusal(e.Code)),
                PrivateRoomMessage.JoinAnswer => (ChatS2COpcodes.AnsPrivateChatRoomJoin, BuildJoinAnswer(room!)),
                PrivateRoomMessage.JoinUser => (ChatS2COpcodes.NfyPrivateChatRoomJoinUser, BuildJoinUser(room!.GuidBytes, e.Uid, e.Text)),
                PrivateRoomMessage.MemberList => (ChatS2COpcodes.AnsPrivateRoomInMember, BuildMemberList(room!)),
                PrivateRoomMessage.ExitMemberInfo => (ChatS2COpcodes.NfyChatRoomExitMmeberInfo, BuildGuidUid(room!.GuidBytes, e.Uid)),
                PrivateRoomMessage.ChangeRoomMaster => (ChatS2COpcodes.NfyPrivateChatRoom_ChangeRoomMaster, BuildUidGuid(e.Uid, room!.GuidBytes)),
                PrivateRoomMessage.LogoutPlayer => (ChatS2COpcodes.NfyLogoutPlayer_PrivateChatRoom, BuildUidGuid(e.Uid, room!.GuidBytes)),
                PrivateRoomMessage.InviteResult => (ChatS2COpcodes.AnsPrivateChatRoomInvite, BuildResult(e.Code)),
                PrivateRoomMessage.InviteInRoomMember => (ChatS2COpcodes.NfyPrivateChatRoomInvate_InRoomMember, BuildJoinUser(room!.GuidBytes, e.Uid, e.Text)),
                PrivateRoomMessage.InviteRoomInfo => (ChatS2COpcodes.NfyInviteRoomInfo, BuildInviteRoomInfo(room!)),
                PrivateRoomMessage.KickResult => (ChatS2COpcodes.AnsPrivateChatRoomInUserKick, BuildResult(e.Code)),
                PrivateRoomMessage.KickToUser => (ChatS2COpcodes.NfyPrivateChatRoomMessage_ToKickUser, room!.GuidBytes.AsSpan().ToArray()),
                PrivateRoomMessage.KickUser => (ChatS2COpcodes.NfyPrivateChatRoomKickUser, BuildGuidUid(room!.GuidBytes, e.Uid)),
                PrivateRoomMessage.ChangePassAnswer => (ChatS2COpcodes.AnsPrivateChatRoomChangePass, BuildChangePassAnswer(e.Code, requestGuid, e.Text)),
                _ => throw new InvalidOperationException($"unmapped private-room message {e.Message}"),
            };
            sends.Add(new Send(session, opcode, body));
        }
    }

 /// <summary>How many rooms list <paramref name="uid"/> as a member: the room slots its client has in use.</summary>
    private static int RoomsHeldLocked(ulong uid) => ById.Values.Count(e => e.Room.Members.ContainsKey(uid));

    
 /// <summary>23519 AnsChatRoomExit: [u16 0][guid16], the request's guid echoed.</summary>
    public static byte[] BuildExitAnswer(ReadOnlySpan<byte> roomGuid)
    {
        var body = new byte[18];
        roomGuid.CopyTo(body.AsSpan(2));
        return body;
    }

 /// <summary>
 /// A refused 23515: [u16 <paramref name="code"/>][u32 26][guid16 0][u64 master 0][u8 0][u8 0], the join
 /// answer's record with every field empty (26 = 16 + 8 + 1 + 1, the u32 the reader's VarBlob needs). Byte for
 /// byte the code-23 refusal (17 00 1a 00 00 00 + 26 zeros); the same record
 /// carries the unframed codes 19 / 26 / 29.
 /// </summary>
    public static byte[] BuildJoinRefusal(ushort code)
    {
        const int emptyRecord = 16 + 8 + 1 + 1;
        var body = new byte[2 + 4 + emptyRecord];
        BinaryPrimitives.WriteUInt16LittleEndian(body, code);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(2), emptyRecord);
        return body;
    }

    private static byte[] BuildJoinAnswer(PrivateRoom room)
    {
        using var w = new PacketWriter();
        w.WriteUInt16(0);
        var lenPos = w.Reserve(4);
        int start = w.Length;
        w.WriteBytes(room.GuidBytes);
        w.WriteUInt64(room.MasterUid);
        WriteName8(w, room.Name);
        WriteName8(w, room.Password);
        w.WriteAt(lenPos, (uint)(w.Length - start));
        return w.ToArray();
    }

    private static byte[] BuildJoinUser(byte[] guid, ulong uid, string name)
    {
        using var w = new PacketWriter();
        w.WriteBytes(guid);
        var lenPos = w.Reserve(4);
        int start = w.Length;
        w.WriteUInt64(uid);
        WriteName8(w, name, MemberNameMaxChars);
        w.WriteAt(lenPos, (uint)(w.Length - start));
        return w.ToArray();
    }

    private static byte[] BuildMemberList(PrivateRoom room)
    {
        using var w = new PacketWriter();
        w.WriteUInt16(0);
        var lenPos = w.Reserve(4);
        int start = w.Length;
        WriteRoomRecord(w, room, MaxMembers);   
        w.WriteAt(lenPos, (uint)(w.Length - start));
        return w.ToArray();
    }

 /// <summary>
 /// 23532 NfyInviteRoomInfo: [u32 len][u8 n][room name UTF-16LE][the 23517 record, capacity field 0]. Reader:
 /// reads the u32-prefixed blob, then the name (n clamped at 50), then the record,
 /// the same call 23517's reader makes.
 /// </summary>
 /// <remarks>
 /// Retail's 23532 is UNENC, 194 bytes in the observed form:
 /// <c>[u32 190][u8 8 "Nemesis'"][u64 master][guid16][u32 0][u16 5]</c> then the
 /// master first and the other four in ascending uid. Its shape is ours byte for
 /// byte except the record's u32, which retail sends as 0 here and as 100 in every 23517: so the invite's
 /// record carries 0. The 23532 consumer reads only the guid, the master and the members, so the
 /// field is cosmetic either way.
 /// </remarks>
    private static byte[] BuildInviteRoomInfo(PrivateRoom room)
    {
        using var w = new PacketWriter();
        var lenPos = w.Reserve(4);
        int start = w.Length;
        WriteName8(w, room.Name, InviteRoomNameMaxChars);
        WriteRoomRecord(w, room, capacity: 0);   
        w.WriteAt(lenPos, (uint)(w.Length - start));
        return w.ToArray();
    }

 /// <summary>
 /// The room record 23517 and 23532 share: [u64 masterUid][guid16][u32 capacity][u16 n]{u64 uid,
 /// name8}, master first, then ascending uid. The u32 is
 /// <paramref name="capacity"/>: 100 in 23517, 0 in retail's 23532.
 /// </summary>
    private static void WriteRoomRecord(PacketWriter w, PrivateRoom room, uint capacity)
    {
        w.WriteUInt64(room.MasterUid);
        w.WriteBytes(room.GuidBytes);
        w.WriteUInt32(capacity);
        w.WriteUInt16((ushort)room.Members.Count);
        if (room.Members.TryGetValue(room.MasterUid, out var master))
            WriteMember(w, master);
        foreach (var m in room.Members.Values)
            if (m.Uid != room.MasterUid)
                WriteMember(w, m);
    }

    private static void WriteMember(PacketWriter w, PrivateRoomMember m)
    {
        w.WriteUInt64(m.Uid);
        WriteName8(w, m.Name, MemberNameMaxChars);
    }

 /// <summary>23522 / 23525: the bare [u16 result] (fixed 2 bytes).</summary>
    private static byte[] BuildResult(ushort result)
    {
        var body = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(body, result);
        return body;
    }

 /// <summary>
 /// 23521 AnsPrivateChatRoomChangePass: [u16 result][u32 len = 17 + 2n][guid16][u8 n][n x UTF-16LE].
 /// Client reader only (no retail frame): a word, then a u32 length + nested
 /// Blob16 (u8 n clamped at 4, 2n bytes). Same [u16][u32 len][record] form as 23515.
 /// </summary>
    private static byte[] BuildChangePassAnswer(ushort result, ReadOnlySpan<byte> roomGuid, string password)
    {
        using var w = new PacketWriter();
        w.WriteUInt16(result);
        var lenPos = w.Reserve(4);
        int start = w.Length;
        w.WriteBytes(roomGuid);
        WriteName8(w, password, PasswordMaxChars);
        w.WriteAt(lenPos, (uint)(w.Length - start));
        return w.ToArray();
    }

    private static byte[] BuildGuidUid(byte[] guid, ulong uid)
    {
        var body = new byte[24];
        guid.CopyTo(body, 0);
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(16), uid);
        return body;
    }

    private static byte[] BuildUidGuid(ulong uid, byte[] guid)
    {
        var body = new byte[24];
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(0), uid);
        guid.CopyTo(body, 8);
        return body;
    }

 /// <summary>[u8 nChars][UTF-16LE], at most <paramref name="maxChars"/> chars: the clamp of the reader
 /// the field goes to, so a longer string cannot throw the reader out of phase.</summary>
    private static void WriteName8(PacketWriter w, string s, int maxChars = byte.MaxValue)
    {
        var bytes = Encoding.Unicode.GetBytes(s);
        int chars = Math.Min(bytes.Length / 2, maxChars);
        w.WriteByte((byte)chars);
        w.WriteBytes(bytes.AsSpan(0, chars * 2));
    }
}
