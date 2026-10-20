using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Chat;
using RO2.Server.Chat.Handlers.Rmi;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Owner operations of a private chat room: password change (23014, answered by 23521), kick (23015, answered by
/// 23522, 23523 and 23524) and invite (23016, answered by 23525, 23526 and 23532) (<see cref="PrivateChatRooms"/>).
/// </summary>
/// <remarks>
/// Expected bodies are written field by field from each message's wire layout, independently of the builders.
/// The room store is static, so every test uses its own room names and uids and looks only at the sends addressed
/// to its own sessions.
/// </remarks>
public class PrivateChatRoomOwnerOpsTests
{
    [Fact]
    public async Task Invite_AddsTheInviteeAndSendsAnswerNoticesAndRoomInfo()
    {
        await using var net = new Loopback();
        var u = NewUids(3);
        ulong master = u[2], member = u[0], invitee = u[1]; 
        var a = await net.ConnectAsync(master, "Alpha");
        var b = await net.ConnectAsync(member, "Bravo");
        var c = await net.ConnectAsync(invitee, "Charlie");
        var room = "inv-" + Guid.NewGuid().ToString("N")[..8];
        var guid = JoinRoom(a, master, "Alpha", room);
        JoinRoom(b, member, "Bravo", room);

        var sends = new List<PrivateChatRooms.Send>();
        var who = ReqPrivateRoomInviteHandler.FindOnline([a, b, c], "charlie");
        var result = PrivateChatRooms.Invite(guid, a, master, who, sends);

        Assert.Equal(PrivateChatRooms.ResultOk, result);
        var mine = Mine(sends, a, b, c);
        Assert.Equal(4, mine.Count);
        
        Assert.Equal((a, ChatS2COpcodes.AnsPrivateChatRoomInvite), (mine[0].Session, mine[0].Opcode));
        Assert.Equal(new byte[] { 0x00, 0x00 }, mine[0].Body);

        
        var notice = Cat(guid, U32(8 + 1 + 2 * 7), U64(invitee), [7], Utf16("Charlie"));
        var notices = mine.Where(s => s.Opcode == ChatS2COpcodes.NfyPrivateChatRoomInvate_InRoomMember).ToList();
        Assert.Equal(2, notices.Count);
        Assert.Contains(notices, s => ReferenceEquals(s.Session, a));
        Assert.Contains(notices, s => ReferenceEquals(s.Session, b));
        Assert.All(notices, s => Assert.Equal(notice, s.Body));

        
        
        
        var members = Cat(
            U64(master), [5], Utf16("Alpha"),
            U64(member), [5], Utf16("Bravo"),
            U64(invitee), [7], Utf16("Charlie"));
        var inviteRecord = Cat(U64(master), guid, U32(0), U16(3), members);
        var record = Cat(U64(master), guid, U32(100), U16(3), members);
        var inner = Cat([(byte)room.Length], Utf16(room), inviteRecord);
        var info = mine.Single(s => s.Opcode == ChatS2COpcodes.NfyInviteRoomInfo);
        Assert.Same(c, info.Session);
        Assert.Equal(Cat(U32(inner.Length), inner), info.Body);

        
        
        Assert.DoesNotContain(mine, s => s.Opcode == ChatS2COpcodes.NfyPrivateChatRoomInvate_JoinMember);
        
        Assert.DoesNotContain(notices, s => ReferenceEquals(s.Session, c));
        Assert.Equal(ChatS2COpcodes.NfyInviteRoomInfo, mine[^1].Opcode);

        
        sends.Clear();
        Assert.True(PrivateChatRooms.MemberList(guid, c, sends));
        Assert.Equal(Cat(U16(0), U32(record.Length), record), Mine(sends, c).Single().Body);

        
        Assert.Contains(PrivateChatRooms.MemberSessions(guid), s => ReferenceEquals(s, c));
    }

    [Fact]
    public async Task Invite_NoticeToMembersIsByteForByteThe23516OfAJoin()
    {
        
        
        await using var net = new Loopback();
        var u = NewUids(3);
        var a = await net.ConnectAsync(u[0], "Alpha");
        var c = await net.ConnectAsync(u[1], "Charlie");
        var joiner = await net.ConnectAsync(u[2], "Delta");
        var room = "eq-" + Guid.NewGuid().ToString("N")[..8];
        var guid = JoinRoom(a, u[0], "Alpha", room);

        var sends = new List<PrivateChatRooms.Send>();
        Assert.Equal(PrivateChatRooms.ResultOk,
            PrivateChatRooms.Invite(guid, a, u[0], ReqPrivateRoomInviteHandler.FindOnline([c], "Charlie"), sends));
        var invited = Mine(sends, a).Single(s => s.Opcode == ChatS2COpcodes.NfyPrivateChatRoomInvate_InRoomMember).Body;

        sends.Clear();
        Assert.Equal(PrivateChatRooms.JoinOutcome.Joined, PrivateChatRooms.Join(joiner, (uint)u[2], u[2], "Delta", room, "", sends));
        var joined = Mine(sends, a).Single(s => s.Opcode == ChatS2COpcodes.NfyPrivateChatRoomJoinUser).Body;

        
        Assert.Equal(Cat(guid, U32(8 + 1 + 2 * 7), U64(u[1]), [7], Utf16("Charlie")), invited);
        Assert.Equal(Cat(guid, U32(8 + 1 + 2 * 5), U64(u[2]), [5], Utf16("Delta")), joined);
    }

    [Fact]
    public async Task MemberNames_AreCutAtTheTwentyCharsOfTheClientsMemberReader()
    {
        
        
        
        await using var net = new Loopback();
        var u = NewUids(3);
        const string longName = "Abcdefghijklmnopqrstuv";   
        var cut = longName[..PrivateChatRooms.MemberNameMaxChars];
        var a = await net.ConnectAsync(u[0], longName);
        var b = await net.ConnectAsync(u[1], "Bravo");
        var c = await net.ConnectAsync(u[2], longName + "x");
        var room = "long-" + Guid.NewGuid().ToString("N")[..8];

        var sends = new List<PrivateChatRooms.Send>();
        Assert.Equal(PrivateChatRooms.JoinOutcome.Joined, PrivateChatRooms.Join(a, (uint)u[0], u[0], longName, room, "", sends));
        var guid = Mine(sends, a).First(s => s.Opcode == ChatS2COpcodes.AnsPrivateChatRoomJoin).Body.AsSpan(6, 16).ToArray();
        Assert.Equal(Cat(guid, U32(8 + 1 + 40), U64(u[0]), [20], Utf16(cut)),
            Mine(sends, a).Single(s => s.Opcode == ChatS2COpcodes.NfyPrivateChatRoomJoinUser).Body);
        JoinRoom(b, u[1], "Bravo", room);

        sends.Clear();
        Assert.Equal(PrivateChatRooms.ResultOk,
            PrivateChatRooms.Invite(guid, a, u[0], ReqPrivateRoomInviteHandler.FindOnline([c], longName + "x"), sends));
        Assert.All(Mine(sends, a, b).Where(s => s.Opcode == ChatS2COpcodes.NfyPrivateChatRoomInvate_InRoomMember),
            s => Assert.Equal(Cat(guid, U32(8 + 1 + 40), U64(u[2]), [20], Utf16(cut)), s.Body));

        
        var record = Cat(U64(u[0]), guid, U32(0), U16(3),
            U64(u[0]), [20], Utf16(cut),
            U64(u[1]), [5], Utf16("Bravo"),
            U64(u[2]), [20], Utf16(cut));
        var inner = Cat([(byte)room.Length], Utf16(room), record);
        Assert.Equal(Cat(U32(inner.Length), inner),
            Mine(sends, c).Single(s => s.Opcode == ChatS2COpcodes.NfyInviteRoomInfo).Body);
    }

    [Fact]
    public async Task Invite_RefusalsAnswerOnlyTheInviterWithTheClientsCodes()
    {
        await using var net = new Loopback();
        var u = NewUids(4);
        var a = await net.ConnectAsync(u[0], "Alpha");
        var b = await net.ConnectAsync(u[1], "Bravo");
        var c = await net.ConnectAsync(u[2], "Charlie");
        var outsider = await net.ConnectAsync(u[3], "Delta");
        var room = "invr-" + Guid.NewGuid().ToString("N")[..8];
        var guid = JoinRoom(a, u[0], "Alpha", room);
        JoinRoom(b, u[1], "Bravo", room);
        var charlie = ReqPrivateRoomInviteHandler.FindOnline([c], "Charlie");

        AssertInviteRefused(PrivateChatRooms.ResultNoRoom, Guid.NewGuid().ToByteArray(), a, u[0], charlie, a, b, c);
        AssertInviteRefused(PrivateChatRooms.ResultNotMember, guid, outsider, u[3], charlie, a, b, c, outsider);
        AssertInviteRefused(PrivateChatRooms.ResultNotMaster, guid, b, u[1], charlie, a, b, c);
        AssertInviteRefused(PrivateChatRooms.ResultNoSuchCharacter, guid, a, u[0], null, a, b, c);
        AssertInviteRefused(PrivateChatRooms.ResultAlreadyInRoom, guid, a, u[0],
            ReqPrivateRoomInviteHandler.FindOnline([b], "Bravo"), a, b, c);

        
        var sends = new List<PrivateChatRooms.Send>();
        Assert.True(PrivateChatRooms.MemberList(guid, a, sends));
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(Mine(sends, a).Single().Body.AsSpan(2 + 4 + 8 + 16 + 4)));
    }

    [Fact]
    public async Task Invite_IsRefusedWithCode9WhenTheInviteeHoldsTheClientsTenRooms()
    {
        
        
        await using var net = new Loopback();
        var u = NewUids(2);
        var a = await net.ConnectAsync(u[0], "Alpha");
        var c = await net.ConnectAsync(u[1], "Charlie");
        var tag = Guid.NewGuid().ToString("N")[..8];
        var guid = JoinRoom(a, u[0], "Alpha", "full-" + tag);
        for (int i = 0; i < PrivateChatRooms.ClientRoomSlots; i++)
            JoinRoom(c, u[1], "Charlie", $"held-{tag}-{i}");

        var sends = new List<PrivateChatRooms.Send>();
        var result = PrivateChatRooms.Invite(guid, a, u[0], ReqPrivateRoomInviteHandler.FindOnline([c], "Charlie"), sends);

        Assert.Equal(PrivateChatRooms.ResultRoomLimit, result);
        var mine = Mine(sends, a, c);
        Assert.Equal((a, ChatS2COpcodes.AnsPrivateChatRoomInvite), (mine.Single().Session, mine.Single().Opcode));
        Assert.Equal(new byte[] { 0x09, 0x00 }, mine.Single().Body);
    }

    [Fact]
    public async Task Kick_RemovesTheTargetAndNotifiesTargetAndRoom()
    {
        await using var net = new Loopback();
        var u = NewUids(3);
        var a = await net.ConnectAsync(u[0], "Alpha");
        var b = await net.ConnectAsync(u[1], "Bravo");
        var c = await net.ConnectAsync(u[2], "Charlie");
        var room = "kick-" + Guid.NewGuid().ToString("N")[..8];
        var guid = JoinRoom(a, u[0], "Alpha", room);
        JoinRoom(b, u[1], "Bravo", room);
        JoinRoom(c, u[2], "Charlie", room);

        var sends = new List<PrivateChatRooms.Send>();
        var result = PrivateChatRooms.Kick(guid, a, u[0], u[1], sends);

        Assert.Equal(PrivateChatRooms.ResultOk, result);
        var mine = Mine(sends, a, b, c);
        Assert.Equal(4, mine.Count);
        Assert.Equal((a, ChatS2COpcodes.AnsPrivateChatRoomInUserKick), (mine[0].Session, mine[0].Opcode));
        Assert.Equal(new byte[] { 0x00, 0x00 }, mine[0].Body);
        var toKicked = mine.Single(s => s.Opcode == ChatS2COpcodes.NfyPrivateChatRoomMessage_ToKickUser);
        Assert.Same(b, toKicked.Session);
        Assert.Equal(guid, toKicked.Body);
        var kicked = mine.Where(s => s.Opcode == ChatS2COpcodes.NfyPrivateChatRoomKickUser).ToList();
        Assert.Equal(2, kicked.Count);
        Assert.Contains(kicked, s => ReferenceEquals(s.Session, a));
        Assert.Contains(kicked, s => ReferenceEquals(s.Session, c));
        Assert.All(kicked, s => Assert.Equal(Cat(guid, U64(u[1])), s.Body));
        
        Assert.Single(mine, s => ReferenceEquals(s.Session, b));

        
        var relay = PrivateChatRooms.MemberSessions(guid);
        Assert.DoesNotContain(relay, s => ReferenceEquals(s, b));
        Assert.Contains(relay, s => ReferenceEquals(s, a));
        Assert.Contains(relay, s => ReferenceEquals(s, c));

        
        sends.Clear();
        Assert.True(PrivateChatRooms.MemberList(guid, a, sends));
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(Mine(sends, a).Single().Body.AsSpan(2 + 4 + 8 + 16 + 4)));
        sends.Clear();
        Assert.Equal(PrivateChatRooms.ResultTargetMissing, PrivateChatRooms.Kick(guid, a, u[0], u[1], sends));
    }

    [Fact]
    public async Task Kick_RefusalsAnswerOnlyTheKickerAndChangeNothing()
    {
        await using var net = new Loopback();
        var u = NewUids(4);
        var a = await net.ConnectAsync(u[0], "Alpha");
        var b = await net.ConnectAsync(u[1], "Bravo");
        var outsider = await net.ConnectAsync(u[3], "Delta");
        var room = "kickr-" + Guid.NewGuid().ToString("N")[..8];
        var guid = JoinRoom(a, u[0], "Alpha", room);
        JoinRoom(b, u[1], "Bravo", room);

        AssertKickRefused(PrivateChatRooms.ResultNoRoom, Guid.NewGuid().ToByteArray(), a, u[0], u[1], a, b);
        AssertKickRefused(PrivateChatRooms.ResultNotMember, guid, outsider, u[3], u[1], a, b, outsider);
        AssertKickRefused(PrivateChatRooms.ResultNotMaster, guid, b, u[1], u[0], a, b);
        AssertKickRefused(PrivateChatRooms.ResultTargetMissing, guid, a, u[0], u[0], a, b);  
        AssertKickRefused(PrivateChatRooms.ResultTargetMissing, guid, a, u[0], u[2], a, b);  

        var sends = new List<PrivateChatRooms.Send>();
        Assert.True(PrivateChatRooms.MemberList(guid, a, sends));
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(Mine(sends, a).Single().Body.AsSpan(2 + 4 + 8 + 16 + 4)));
    }

    [Fact]
    public async Task ChangePassword_EchoesThePasswordAndGatesTheNextJoin()
    {
        await using var net = new Loopback();
        var u = NewUids(2);
        var a = await net.ConnectAsync(u[0], "Alpha");
        var d = await net.ConnectAsync(u[1], "Delta");
        var room = "pw-" + Guid.NewGuid().ToString("N")[..8];
        var guid = JoinRoom(a, u[0], "Alpha", room);

        var sends = new List<PrivateChatRooms.Send>();
        Assert.Equal(PrivateChatRooms.ResultOk, PrivateChatRooms.ChangePassword(guid, a, u[0], "0427", sends));
        var answer = Mine(sends, a).Single();
        Assert.Equal(ChatS2COpcodes.AnsPrivateChatRoomChangePass, answer.Opcode);
        
        Assert.Equal(Cat(U16(0), U32(17 + 8), guid, [4], Utf16("0427")), answer.Body);

        
        sends.Clear();
        Assert.Equal(PrivateChatRooms.JoinOutcome.WrongPassword,
            PrivateChatRooms.Join(d, (uint)u[1], u[1], "Delta", room, "", sends));
        sends.Clear();
        Assert.Equal(PrivateChatRooms.JoinOutcome.Joined,
            PrivateChatRooms.Join(d, (uint)u[1], u[1], "Delta", room, "0427", sends));
        var joinAnswer = Mine(sends, d).First(s => s.Opcode == ChatS2COpcodes.AnsPrivateChatRoomJoin).Body;
        var joinInner = Cat(guid, U64(u[0]), [(byte)room.Length], Utf16(room), [4], Utf16("0427"));
        Assert.Equal(Cat(U16(0), U32(joinInner.Length), joinInner), joinAnswer);
    }

    [Fact]
    public async Task ChangePassword_RefusalsKeepTheShapeWithAnEmptyPassword()
    {
        await using var net = new Loopback();
        var u = NewUids(3);
        var a = await net.ConnectAsync(u[0], "Alpha");
        var b = await net.ConnectAsync(u[1], "Bravo");
        var outsider = await net.ConnectAsync(u[2], "Delta");
        var room = "pwr-" + Guid.NewGuid().ToString("N")[..8];
        var guid = JoinRoom(a, u[0], "Alpha", room);
        JoinRoom(b, u[1], "Bravo", room);
        var unknown = Guid.NewGuid().ToByteArray();

        AssertPasswordRefused(PrivateChatRooms.ResultNoRoom, unknown, a, u[0], "1234");
        AssertPasswordRefused(PrivateChatRooms.ResultNotMember, guid, outsider, u[2], "1234");
        AssertPasswordRefused(PrivateChatRooms.ResultNotMaster, guid, b, u[1], "1234");
        AssertPasswordRefused(PrivateChatRooms.ResultBadPassword, guid, a, u[0], "12a4");
        AssertPasswordRefused(PrivateChatRooms.ResultBadPassword, guid, a, u[0], "12345");
        AssertPasswordRefused(PrivateChatRooms.ResultBadPassword, guid, a, u[0], "");

        
        var lateUid = NewUids(1)[0];
        var late = await net.ConnectAsync(lateUid, "Echo");
        var sends = new List<PrivateChatRooms.Send>();
        Assert.Equal(PrivateChatRooms.JoinOutcome.Joined,
            PrivateChatRooms.Join(late, (uint)lateUid, lateUid, "Echo", room, "", sends));
    }

    [Fact]
    public void Requests_ParseAsTheClientsWritersProduceThem()
    {
        var guid = Guid.NewGuid().ToByteArray();

        
        var inviteInner = Cat(U16(15), guid, [7], Utf16("Charlie"));
        Assert.True(ReqPrivateRoomInviteHandler.TryParse(Cat(U32(inviteInner.Length), inviteInner), out var g1, out var name));
        Assert.Equal(guid, g1);
        Assert.Equal("Charlie", name);
        Assert.False(ReqPrivateRoomInviteHandler.TryParse(Cat(U32(inviteInner.Length + 1), inviteInner), out _, out _));
        
        var trailing = Cat(inviteInner, [0x41, 0x00]);
        Assert.False(ReqPrivateRoomInviteHandler.TryParse(Cat(U32(trailing.Length), trailing), out _, out _));

        
        var passInner = Cat(guid, [4], Utf16("0427"));
        Assert.True(ReqPrivateRoomChangePassHandler.TryParse(Cat(U32(passInner.Length), passInner), out var g2, out var pw));
        Assert.Equal(guid, g2);
        Assert.Equal("0427", pw);
        Assert.False(ReqPrivateRoomChangePassHandler.TryParse(passInner, out _, out _));

        
        Assert.True(ReqPrivateRoomKickHandler.TryParse(Cat(guid, U64(0x0E043DA700000042)), out var g3, out var uid));
        Assert.Equal(guid, g3);
        Assert.Equal(0x0E043DA700000042UL, uid);
        Assert.False(ReqPrivateRoomKickHandler.TryParse(Cat(guid, U64(1), [0]), out _, out _));
    }

    [Fact]
    public async Task FindOnline_MatchesTheMessengerSessionByNameIgnoringCase()
    {
        await using var net = new Loopback();
        var u = NewUids(3);
        var older = await net.ConnectAsync(u[0], "Charlie");
        var newer = await net.ConnectAsync(u[1], "Charlie");
        var other = await net.ConnectAsync(u[2], "Bravo");
        var noLogin = await net.ConnectAsync(null, "Zulu");

        var found = Assert.NotNull(ReqPrivateRoomInviteHandler.FindOnline([older, other, newer, noLogin], "CHARLIE"));
        Assert.Same(newer, found.Session);
        Assert.Equal(u[1], found.Uid);
        Assert.Equal((uint)u[1], found.CharacterId);
        Assert.Equal("Charlie", found.Name);
        Assert.Null(ReqPrivateRoomInviteHandler.FindOnline([older, newer, other, noLogin], "Zulu"));
        Assert.Null(ReqPrivateRoomInviteHandler.FindOnline([older, newer, other], ""));
    }

    
    private static void AssertInviteRefused(ushort code, byte[] guid, ProudSession inviter, ulong inviterUid,
        PrivateChatRooms.Invitee? invitee, params ProudSession[] watched)
    {
        var sends = new List<PrivateChatRooms.Send>();
        Assert.Equal(code, PrivateChatRooms.Invite(guid, inviter, inviterUid, invitee, sends));
        var mine = Mine(sends, watched);
        Assert.Single(mine);
        Assert.Same(inviter, mine[0].Session);
        Assert.Equal(ChatS2COpcodes.AnsPrivateChatRoomInvite, mine[0].Opcode);
        Assert.Equal(U16(code), mine[0].Body);
    }

    private static void AssertKickRefused(ushort code, byte[] guid, ProudSession kicker, ulong kickerUid, ulong target,
        params ProudSession[] watched)
    {
        var sends = new List<PrivateChatRooms.Send>();
        Assert.Equal(code, PrivateChatRooms.Kick(guid, kicker, kickerUid, target, sends));
        var mine = Mine(sends, watched);
        Assert.Single(mine);
        Assert.Same(kicker, mine[0].Session);
        Assert.Equal(ChatS2COpcodes.AnsPrivateChatRoomInUserKick, mine[0].Opcode);
        Assert.Equal(U16(code), mine[0].Body);
    }

    private static void AssertPasswordRefused(ushort code, byte[] guid, ProudSession requester, ulong uid, string password)
    {
        var sends = new List<PrivateChatRooms.Send>();
        Assert.Equal(code, PrivateChatRooms.ChangePassword(guid, requester, uid, password, sends));
        var answer = Mine(sends, requester).Single();
        Assert.Equal(ChatS2COpcodes.AnsPrivateChatRoomChangePass, answer.Opcode);
        
        Assert.Equal(Cat(U16(code), U32(17), guid, [0]), answer.Body);
    }

 /// <summary>23011 through the store; returns the room guid from the 23515 answer ([u16][u32][guid]).</summary>
    private static byte[] JoinRoom(ProudSession session, ulong uid, string name, string room)
    {
        var sends = new List<PrivateChatRooms.Send>();
        Assert.Equal(PrivateChatRooms.JoinOutcome.Joined, PrivateChatRooms.Join(session, (uint)uid, uid, name, room, "", sends));
        var answer = Mine(sends, session).First(s => s.Opcode == ChatS2COpcodes.AnsPrivateChatRoomJoin);
        return answer.Body.AsSpan(6, 16).ToArray();
    }

 /// <summary>The sends addressed to this test's sessions, in order (another test's pruning may add its own).</summary>
    private static List<PrivateChatRooms.Send> Mine(List<PrivateChatRooms.Send> sends, params ProudSession[] sessions)
        => sends.Where(s => sessions.Any(x => ReferenceEquals(x, s.Session))).ToList();

 /// <summary>Distinct GID-like uids (above 2^32, as every member uid), unique to this call.</summary>
    private static ulong[] NewUids(int n)
    {
        ulong band = 0x0E00000000000000UL | ((ulong)Random.Shared.NextInt64(1, 0xFFFFFF) << 24);
        var uids = new ulong[n];
        for (int i = 0; i < n; i++) uids[i] = band + (ulong)(i + 1);
        return uids;
    }

    private static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
    private static byte[] U16(int v) { var b = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)v); return b; }
    private static byte[] U32(int v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)v); return b; }
    private static byte[] U64(ulong v) { var b = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(b, v); return b; }
    private static byte[] Utf16(string s) => Encoding.Unicode.GetBytes(s);

 /// <summary>Real connected <see cref="ProudSession"/>s on the accepting end of loopback sockets.</summary>
    private sealed class Loopback : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly List<Socket> _clients = [];
        private readonly List<ProudSession> _sessions = [];
        private uint _nextId = 7000;

        public Loopback() => _listener.Start();

 /// <summary>A session as ChatAuthHandler leaves it: CharacterId, MessengerUid, Username. A null uid
 /// is a connection that has not sent its 23001.</summary>
        public async Task<ProudSession> ConnectAsync(ulong? uid, string name)
        {
            var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            var accept = _listener.AcceptSocketAsync();
            await client.ConnectAsync((IPEndPoint)_listener.LocalEndpoint);
            var accepted = await accept;
            var session = new ProudSession(accepted, ++_nextId, NullLogger.Instance) { Username = name };
            if (uid is ulong full)
            {
                session.CharacterId = (uint)full;
                MessengerUid.Set(session, full);
            }
            _clients.Add(client);
            _sessions.Add(session);
            return session;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var s in _sessions) await s.DisposeAsync();
            foreach (var c in _clients) c.Dispose();
            _listener.Stop();
        }
    }
}
