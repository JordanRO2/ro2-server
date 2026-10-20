using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Chat;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// A refused 23011 ReqPrivateChatRoomJoin: the server answers with a lone 23515 AnsPrivateChatRoomJoin carrying
/// [u16 code][u32 26][zeroed record] (<see cref="PrivateChatRooms.BuildJoinRefusal"/>), with no 23516 or 23517.
/// </summary>
/// <remarks>
/// The already-participating refusal (code 23, string 9516) is sent for a uid that the room still lists. The codes
/// 26, 19 and 29 are checked against the client's result strings (19 to 9736, 26 to 9735, 29 to 9758). The room store
/// is static, so every test uses its own room names and uids and looks only at its own sessions.
/// </remarks>
public class PrivateChatRoomJoinRefusalTests
{
 /// <summary>Expected 23515 refusal body for the already-participating case, 32 bytes.</summary>
    private const string RetailRefusal23 = "17001a0000000000000000000000000000000000000000000000000000000000";

    [Fact]
    public void Refusal_IsTheRetailBody()
    {
        Assert.Equal(RetailRefusal23, Hex(PrivateChatRooms.BuildJoinRefusal(PrivateChatRooms.ResultAlreadyInRoom)));
        
        foreach (var code in new[] { PrivateChatRooms.ResultWrongPassword, PrivateChatRooms.ResultPasswordRequired, PrivateChatRooms.ResultRoomFull })
        {
            var body = PrivateChatRooms.BuildJoinRefusal(code);
            Assert.Equal(32, body.Length);
            Assert.Equal(code, BinaryPrimitives.ReadUInt16LittleEndian(body));
            Assert.Equal(RetailRefusal23[4..], Hex(body)[4..]);
        }
    }

    [Fact]
    public async Task UidStillListed_IsRefusedWith23_AndTheMembershipIsUntouched()
    {
        
        
        await using var net = new Loopback();
        var u = NewUids(2);
        var master = await net.ConnectAsync(u[0], "Alpha");
        var oldLogin = await net.ConnectAsync(u[1], "Bravo");
        var newLogin = await net.ConnectAsync(u[1], "Bravo");
        var room = "prontch-" + Guid.NewGuid().ToString("N")[..8];
        var guid = JoinRoom(master, u[0], "Alpha", room);
        JoinRoom(oldLogin, u[1], "Bravo", room);

        var sends = new List<PrivateChatRooms.Send>();
        Assert.Equal(PrivateChatRooms.JoinOutcome.AlreadyInRoom,
            PrivateChatRooms.Join(newLogin, (uint)u[1], u[1], "Bravo", room, "", sends));

        
        var mine = Mine(sends, master, oldLogin, newLogin);
        var only = Assert.Single(mine);
        Assert.Same(newLogin, only.Session);
        Assert.Equal(ChatS2COpcodes.AnsPrivateChatRoomJoin, only.Opcode);
        Assert.Equal(RetailRefusal23, Hex(only.Body));

        
        var members = PrivateChatRooms.MemberSessions(guid);
        Assert.Contains(members, s => ReferenceEquals(s, oldLogin));
        Assert.DoesNotContain(members, s => ReferenceEquals(s, newLogin));
    }

    [Fact]
    public async Task SameConnectionAskingAgain_IsRefusedWith23()
    {
        
        await using var net = new Loopback();
        var u = NewUids(1);
        var a = await net.ConnectAsync(u[0], "Alpha");
        var room = "again-" + Guid.NewGuid().ToString("N")[..8];
        JoinRoom(a, u[0], "Alpha", room);

        var sends = new List<PrivateChatRooms.Send>();
        Assert.Equal(PrivateChatRooms.JoinOutcome.AlreadyInRoom,
            PrivateChatRooms.Join(a, (uint)u[0], u[0], "Alpha", room, "", sends));
        Assert.Equal(RetailRefusal23, Hex(Assert.Single(Mine(sends, a)).Body));
    }

    [Fact]
    public async Task OnceTheOldConnectionIsGone_TheRejoinSucceeds()
    {
        
        
        await using var net = new Loopback();
        var u = NewUids(2);
        var master = await net.ConnectAsync(u[0], "Alpha");
        var oldLogin = await net.ConnectAsync(u[1], "Bravo");
        var newLogin = await net.ConnectAsync(u[1], "Bravo");
        var room = "dq-" + Guid.NewGuid().ToString("N")[..8];
        var guid = JoinRoom(master, u[0], "Alpha", room);
        JoinRoom(oldLogin, u[1], "Bravo", room);
        await oldLogin.DisposeAsync();

        var sends = new List<PrivateChatRooms.Send>();
        Assert.Equal(PrivateChatRooms.JoinOutcome.Joined,
            PrivateChatRooms.Join(newLogin, (uint)u[1], u[1], "Bravo", room, "", sends));
        var mine = Mine(sends, master, newLogin);
        
        Assert.Equal(ChatS2COpcodes.NfyLogoutPlayer_PrivateChatRoom, mine[0].Opcode);
        var answer = mine.Single(s => s.Opcode == ChatS2COpcodes.AnsPrivateChatRoomJoin);
        Assert.Same(newLogin, answer.Session);
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(answer.Body));
        Assert.Equal(guid, answer.Body.AsSpan(6, 16).ToArray());
        Assert.Equal(2, mine.Count(s => s.Opcode == ChatS2COpcodes.NfyPrivateChatRoomJoinUser));
        Assert.Single(mine, s => s.Opcode == ChatS2COpcodes.AnsPrivateRoomInMember && ReferenceEquals(s.Session, newLogin));
    }

    [Fact]
    public async Task Password_EmptyGets26_WrongGets19()
    {
        await using var net = new Loopback();
        var u = NewUids(2);
        var a = await net.ConnectAsync(u[0], "Alpha");
        var d = await net.ConnectAsync(u[1], "Delta");
        var room = "pwj-" + Guid.NewGuid().ToString("N")[..8];
        var guid = JoinRoom(a, u[0], "Alpha", room);
        Assert.Equal(PrivateChatRooms.ResultOk, PrivateChatRooms.ChangePassword(guid, a, u[0], "0427", []));

        AssertRefused(d, u[1], room, "", PrivateChatRooms.JoinOutcome.WrongPassword, PrivateChatRooms.ResultPasswordRequired, a);
        AssertRefused(d, u[1], room, "1111", PrivateChatRooms.JoinOutcome.WrongPassword, PrivateChatRooms.ResultWrongPassword, a);

        var sends = new List<PrivateChatRooms.Send>();
        Assert.Equal(PrivateChatRooms.JoinOutcome.Joined, PrivateChatRooms.Join(d, (uint)u[1], u[1], "Delta", room, "0427", sends));
    }

    [Fact]
    public async Task AlreadyListed_IsCheckedBeforeThePassword()
    {
        
        await using var net = new Loopback();
        var u = NewUids(1);
        var a = await net.ConnectAsync(u[0], "Alpha");
        var again = await net.ConnectAsync(u[0], "Alpha");
        var room = "order-" + Guid.NewGuid().ToString("N")[..8];
        var guid = JoinRoom(a, u[0], "Alpha", room);
        Assert.Equal(PrivateChatRooms.ResultOk, PrivateChatRooms.ChangePassword(guid, a, u[0], "0427", []));

        AssertRefused(again, u[0], room, "9999", PrivateChatRooms.JoinOutcome.AlreadyInRoom, PrivateChatRooms.ResultAlreadyInRoom, a);
    }

    [Fact]
    public async Task FullRoom_Gets29()
    {
        await using var net = new Loopback();
        var u = NewUids(PrivateChatRooms.MaxMembers + 1);
        var room = "full-" + Guid.NewGuid().ToString("N")[..8];
        var sessions = new List<ProudSession>();
        for (int i = 0; i < PrivateChatRooms.MaxMembers; i++)
        {
            var s = await net.ConnectAsync(u[i], "M" + i);
            JoinRoom(s, u[i], "M" + i, room);
            sessions.Add(s);
        }
        var late = await net.ConnectAsync(u[^1], "Late");
        sessions.Add(late);

        var sends = new List<PrivateChatRooms.Send>();
        Assert.Equal(PrivateChatRooms.JoinOutcome.Full, PrivateChatRooms.Join(late, (uint)u[^1], u[^1], "Late", room, "", sends));
        var only = Assert.Single(Mine(sends, [.. sessions]));
        Assert.Same(late, only.Session);
        Assert.Equal(PrivateChatRooms.BuildJoinRefusal(PrivateChatRooms.ResultRoomFull), only.Body);
    }

    private static void AssertRefused(ProudSession session, ulong uid, string room, string password,
        PrivateChatRooms.JoinOutcome outcome, ushort code, params ProudSession[] others)
    {
        var sends = new List<PrivateChatRooms.Send>();
        Assert.Equal(outcome, PrivateChatRooms.Join(session, (uint)uid, uid, session.Username ?? "", room, password, sends));
        var only = Assert.Single(Mine(sends, [session, .. others]));
        Assert.Same(session, only.Session);
        Assert.Equal(ChatS2COpcodes.AnsPrivateChatRoomJoin, only.Opcode);
        Assert.Equal(PrivateChatRooms.BuildJoinRefusal(code), only.Body);
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
        ulong band = 0x0D00000000000000UL | ((ulong)Random.Shared.NextInt64(1, 0xFFFFFF) << 24);
        var uids = new ulong[n];
        for (int i = 0; i < n; i++) uids[i] = band + (ulong)(i + 1);
        return uids;
    }

    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

 /// <summary>Real connected <see cref="ProudSession"/>s on the accepting end of loopback sockets.</summary>
    private sealed class Loopback : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly List<Socket> _clients = [];
        private readonly List<ProudSession> _sessions = [];
        private uint _nextId = 9000;

        public Loopback() => _listener.Start(backlog: 256);

 /// <summary>A session as ChatAuthHandler leaves it: CharacterId, MessengerUid, Username.</summary>
        public async Task<ProudSession> ConnectAsync(ulong uid, string name)
        {
            var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            var accept = _listener.AcceptSocketAsync();
            await client.ConnectAsync((IPEndPoint)_listener.LocalEndpoint);
            var accepted = await accept;
            var session = new ProudSession(accepted, ++_nextId, NullLogger.Instance) { Username = name };
            session.CharacterId = (uint)uid;
            MessengerUid.Set(session, uid);
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
