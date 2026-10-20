using Microsoft.Extensions.Logging.Abstractions;
using NameFilterRow = RO2.Domain.GameData.Tables.NameFilter;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Chat.Handlers.Rmi;
using RO2.Server.Chat;
using RO2.Server.Zone.Services;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Net;
using System.Text.Json;
using Xunit;
using static RO2.Tests.Packets.ReauditShared;

namespace RO2.Tests.Packets;

/// <summary>
/// Layout test for NfyInviteRoomInfo (23532), the room notification sent in answer to a 23016 invite.
/// </summary>
public class PrivateChatRoomInviteReauditTests
{
    
    [Fact]
    public async Task InviteRoomInfo_23532_IsRetailsFrame_UnderOurRoomGuid()
    {
        
        
        
        
        const string retail =
            "be000000084e0065006d00650073006900730027000ebbeaa7b3b6e00db7b6b9f09496694490fa1d82c1cf25c40000000005000ebb" +
            "eaa7b3b6e00d07780048006f006d00750072006100f0dcb6a7b3b6e00d0b4c00750075006b004800650072007300730065006e0030" +
            "39eaa7b3b6e00d0852006800690065007a006300690065000fbbeaa7b3b6e00d0a7800440061006d0073006c006500740074006500" +
            "fff4187db3e8c9370d52003000520030004e0030004100490049005a00300052003000";
        var frame = Convert.FromHexString(retail);
        Assert.Equal(194, frame.Length);

        (ulong uid, string name)[] members =
        [
            (0x0DE0B6B3A7EABB0EUL, "xHomura"),
            (0x0DE0B6B3A7B6DCF0UL, "LuukHerssen"),
            (0x0DE0B6B3A7EA3930UL, "Rhiezcie"),
            (0x0DE0B6B3A7EABB0FUL, "xDamslette"),
        ];
        (ulong uid, string name) invitee = (0x37C9E8B37D18F4FFUL, "R0R0N0AIIZ0R0");
        const string room = "Nemesis'";

        await using var net = new Loopback();
        var sessions = new List<ProudSession>();
        foreach (var (uid, name) in members) sessions.Add(await net.ConnectAsync(uid, name));
        var c = await net.ConnectAsync(invitee.uid, invitee.name);

        var sends = new List<PrivateChatRooms.Send>();
        for (int i = 0; i < members.Length; i++)
            Assert.Equal(PrivateChatRooms.JoinOutcome.Joined,
                PrivateChatRooms.Join(sessions[i], (uint)members[i].uid, members[i].uid, members[i].name, room, "", sends));
        var guid = sends.First(s => ReferenceEquals(s.Session, sessions[0]) && s.Opcode == ChatS2COpcodes.AnsPrivateChatRoomJoin)
            .Body.AsSpan(6, 16).ToArray();

        sends.Clear();
        Assert.Equal(PrivateChatRooms.ResultOk, PrivateChatRooms.Invite(guid, sessions[0], members[0].uid,
            ReqPrivateRoomInviteHandler.FindOnline([c], invitee.name), sends));
        var info = sends.Single(s => ReferenceEquals(s.Session, c) && s.Opcode == ChatS2COpcodes.NfyInviteRoomInfo).Body;

        
        guid.CopyTo(frame, 29);
        Assert.Equal(Hex(frame), Hex(info));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(45)));

        
        sends.Clear();
        Assert.True(PrivateChatRooms.MemberList(guid, c, sends));
        var list = sends.Single(s => ReferenceEquals(s.Session, c)).Body;
        Assert.Equal(100u, BinaryPrimitives.ReadUInt32LittleEndian(list.AsSpan(6 + 8 + 16)));
        
        Assert.Equal(Hex(info.AsSpan(21 + 28)), Hex(list.AsSpan(6 + 28)));
    }

 /// <summary>Real connected <see cref="ProudSession"/>s on the accepting end of loopback sockets.</summary>
    private sealed class Loopback : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly List<Socket> _clients = [];
        private readonly List<ProudSession> _sessions = [];
        private uint _nextId = 7500;

        public Loopback() => _listener.Start();

        public async Task<ProudSession> ConnectAsync(ulong uid, string name)
        {
            var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            var accept = _listener.AcceptSocketAsync();
            await client.ConnectAsync((IPEndPoint)_listener.LocalEndpoint);
            var accepted = await accept;
            var session = new ProudSession(accepted, ++_nextId, NullLogger.Instance) { Username = name, CharacterId = (uint)uid };
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
