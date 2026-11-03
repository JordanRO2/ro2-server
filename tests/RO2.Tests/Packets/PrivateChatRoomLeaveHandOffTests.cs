using RO2.Chats.Domain;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Chat;
using Xunit;

namespace RO2.Tests.Packets;

// B15 S1: PrivateChatRooms.Leave (23019 exit), which no existing test drives: the member notice, the lowest-remaining-uid master hand-off, and the
// room dissolving when its last member leaves. The room store is static, so each test uses its own room name and uids.
public class PrivateChatRoomLeaveHandOffTests
{
    [Fact]
    public async Task MasterLeaves_NoticeGoesToTheRemainingMembers_AndTheLowestRemainingUidBecomesMaster()
    {
        await using var net = new Loopback();
        var u = NewUids(3);
        ulong master = u[1], low = u[0], high = u[2];   // join order differs from uid order; the hand-off is by uid
        var m = await net.ConnectAsync(master, "Master");
        var h = await net.ConnectAsync(high, "High");
        var l = await net.ConnectAsync(low, "Low");
        var room = "lv-" + Guid.NewGuid().ToString("N")[..8];
        var guid = JoinRoom(m, master, "Master", room);
        JoinRoom(h, high, "High", room);
        JoinRoom(l, low, "Low", room);

        var sends = new List<PrivateChatRooms.Send>();
        Assert.True(PrivateChatRooms.Leave(guid, (uint)master, sends));
        var mine = sends.Where(s => new[] { m, h, l }.Any(x => ReferenceEquals(x, s.Session))).ToList();

        // 23504 [guid][u64 uid] to each remaining member (not the leaver), then 23533 [u64 newMaster][guid] to each.
        var gone = guid.Concat(U64(master)).ToArray();
        var exits = mine.Where(s => s.Opcode == ChatS2COpcodes.NfyChatRoomExitMmeberInfo).ToList();
        Assert.Equal(2, exits.Count);
        Assert.DoesNotContain(exits, s => ReferenceEquals(s.Session, m));
        Assert.All(exits, s => Assert.Equal(gone, s.Body));

        var change = U64(low).Concat(guid).ToArray();
        var changes = mine.Where(s => s.Opcode == ChatS2COpcodes.NfyPrivateChatRoom_ChangeRoomMaster).ToList();
        Assert.Equal(2, changes.Count);
        Assert.Contains(changes, s => ReferenceEquals(s.Session, h));
        Assert.Contains(changes, s => ReferenceEquals(s.Session, l));
        Assert.All(changes, s => Assert.Equal(change, s.Body));
        Assert.True(mine.FindLastIndex(s => s.Opcode == ChatS2COpcodes.NfyChatRoomExitMmeberInfo)
                    < mine.FindIndex(s => s.Opcode == ChatS2COpcodes.NfyPrivateChatRoom_ChangeRoomMaster));

        // The new master really is the owner: it may change the password, the old master (no longer a member) may not.
        Assert.Equal(PrivateChatRooms.ResultOk, PrivateChatRooms.ChangePassword(guid, l, low, "1234", []));
        Assert.Equal(PrivateChatRooms.ResultNotMember, PrivateChatRooms.ChangePassword(guid, m, master, "1234", []));
    }

    [Fact]
    public async Task NonMasterLeaves_NoHandOff_AndAMissingRoomOrNonMemberReturnsFalse()
    {
        await using var net = new Loopback();
        var u = NewUids(3);
        var a = await net.ConnectAsync(u[0], "Alpha");
        var b = await net.ConnectAsync(u[1], "Bravo");
        var room = "lv2-" + Guid.NewGuid().ToString("N")[..8];
        var guid = JoinRoom(a, u[0], "Alpha", room);
        JoinRoom(b, u[1], "Bravo", room);

        var sends = new List<PrivateChatRooms.Send>();
        Assert.False(PrivateChatRooms.Leave(Guid.NewGuid().ToByteArray(), (uint)u[1], sends));
        Assert.False(PrivateChatRooms.Leave(guid, (uint)u[2], sends));          // never a member
        Assert.True(PrivateChatRooms.Leave(guid, (uint)u[1], sends));
        var mine = sends.Where(s => ReferenceEquals(s.Session, a) || ReferenceEquals(s.Session, b)).ToList();
        Assert.Single(mine);
        Assert.Same(a, mine[0].Session);
        Assert.Equal(ChatS2COpcodes.NfyChatRoomExitMmeberInfo, mine[0].Opcode);
        Assert.Equal(PrivateChatRooms.ResultOk, PrivateChatRooms.ChangePassword(guid, a, u[0], "1", []));   // still the master
    }

    [Fact]
    public async Task TheLastMemberLeaving_DissolvesTheRoom_AndTheNameIsFreeToCreateAgain()
    {
        await using var net = new Loopback();
        var u = NewUids(2);
        var a = await net.ConnectAsync(u[0], "Alpha");
        var b = await net.ConnectAsync(u[1], "Bravo");
        var room = "lv3-" + Guid.NewGuid().ToString("N")[..8];
        var guid = JoinRoom(a, u[0], "Alpha", room);
        Assert.Equal(PrivateChatRooms.ResultOk, PrivateChatRooms.ChangePassword(guid, a, u[0], "9999", []));

        var sends = new List<PrivateChatRooms.Send>();
        Assert.True(PrivateChatRooms.Leave(guid, (uint)u[0], sends));
        Assert.DoesNotContain(sends, s => ReferenceEquals(s.Session, a) || ReferenceEquals(s.Session, b));
        Assert.False(PrivateChatRooms.Leave(guid, (uint)u[0], sends));
        Assert.Equal(PrivateChatRooms.ResultNoRoom, PrivateChatRooms.ChangePassword(guid, a, u[0], "1", []));

        // A fresh room under the same name: new guid, and the old password is gone (a passwordless join creates and enters it).
        var again = JoinRoom(b, u[1], "Bravo", room);
        Assert.NotEqual(guid, again);
    }

    private static byte[] JoinRoom(ProudSession session, ulong uid, string name, string room)
    {
        var sends = new List<PrivateChatRooms.Send>();
        Assert.Equal(JoinOutcome.Joined, PrivateChatRooms.Join(session, (uint)uid, uid, name, room, "", sends));
        return sends.First(s => ReferenceEquals(s.Session, session) && s.Opcode == ChatS2COpcodes.AnsPrivateChatRoomJoin)
            .Body.AsSpan(6, 16).ToArray();
    }

    private static ulong[] NewUids(int n)
    {
        ulong band = 0x0E00000000000000UL | ((ulong)Random.Shared.NextInt64(1, 0xFFFFFF) << 24);
        var uids = new ulong[n];
        for (int i = 0; i < n; i++) uids[i] = band + (ulong)(i + 1);
        return uids;
    }

    private static byte[] U64(ulong v) { var b = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(b, v); return b; }

    private sealed class Loopback : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly List<Socket> _clients = [];
        private readonly List<ProudSession> _sessions = [];
        private uint _nextId = 9000;

        public Loopback() => _listener.Start();

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
