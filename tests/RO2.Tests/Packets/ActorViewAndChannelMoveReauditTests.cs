using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Application.Configuration;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Configuration;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;
using static RO2.Tests.Packets.ReauditShared;

namespace RO2.Tests.Packets;

/// <summary>
/// Tests for the view and channel-move handlers: 1305 MovePC is ignored, 1307 for an out-of-view PC gets no 2643,
/// 1420 refusals return result codes 1018 or 1014, and non-GM '/' lines get no answer.
/// The 1348 level gate is covered in CraftLearnLevelGateTests.
/// </summary>
public class ActorViewAndChannelMoveReauditTests
{
    
 /// <summary>
 /// RMI 1305 MovePC has no effect. A crafted 1305 must not move the PC or send anything. No retail frame exists,
 /// so the body is the shape the old handler parsed.
 /// </summary>
    [Fact]
    public async Task MovePC1305_ChangesNoStateAndSendsNothing()
    {
        await using var net = new Loopback();
        var (session, client) = await net.ConnectAsync();
        var players = new PlayerStateManager(new NoDirtySink());
        var player = new PlayerState { SessionId = session.SessionId, EntityId = PlayerStateManager.BuildActorHandle(session.SessionId),
            MapId = 1100, X = 10f, Y = 20f, Z = 30f, IsMoving = false };
        players.AddPlayer(session.SessionId, player);
        var handler = new ProbeMovement(players);

        var body = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), 1);           
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(8), 999f);       
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(12), 999f);      
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(16), 999f);      
        await handler.RunAsync(session, body, player);

        Assert.Equal((10f, 20f, 30f, false), (player.X, player.Y, player.Z, player.IsMoving));
        Assert.Equal(0, client.Available);
        Assert.Equal(ZoneC2SOpcodes.MovePC, handler.Opcode); 
    }

    
 /// <summary>
 /// Answer rule for 1307 views: a PC last seen in a 2504 is answered almost always, while a PC last seen in a 2505,
 /// or only before the latest 2311, is answered much less often.
 /// </summary>
    [Fact]
    public void IsInViewOf_NeedsTheSameMapAndALoadedMap()
    {
        var me = new PlayerState { MapId = 1100, MapLoaded = true };
        Assert.True(ReqActorDetailInfoHandler.IsInViewOf(me, new PlayerState { MapId = 1100, MapLoaded = true }));
        Assert.False(ReqActorDetailInfoHandler.IsInViewOf(me, new PlayerState { MapId = 1111, MapLoaded = true }));   
        Assert.False(ReqActorDetailInfoHandler.IsInViewOf(me, new PlayerState { MapId = 1100, MapLoaded = false }));  
        Assert.False(ReqActorDetailInfoHandler.IsInViewOf(me,
            new PlayerState { MapId = 1100, MapLoaded = true, PendingGliderDismount = true }));                      
    }

 /// <summary>
 /// Live path: a 1307 naming a PC on another map gets no answer, but the selection is still recorded.
 /// A 1307 naming a PC in view gets exactly one 2643. The request body is 14 bytes, with the requester at body+6
 /// and the target at body+10.
 /// </summary>
    [Fact]
    public async Task ReqActorDetailInfo1307_OutOfViewPcGetsNo2643_InViewPcGetsOne()
    {
        await using var net = new Loopback();
        var (session, client) = await net.ConnectAsync();
        var players = new PlayerStateManager(new NoDirtySink());
        PlayerState Pc(uint s, uint map, bool loaded)
        {
            var p = new PlayerState { SessionId = s, EntityId = PlayerStateManager.BuildActorHandle(s), MapId = map, MapLoaded = loaded,
                Hp = 500, MaxHp = 800, Mp = 40, MaxMp = 90, Level = 31 };
            players.AddPlayer(s, p);
            return p;
        }
        var me = Pc(session.SessionId, 1100, true);
        var away = Pc(0xA61001, 1111, true);
        var loading = Pc(0xA61002, 1100, false);
        var near = Pc(0xA61003, 1100, true);
        var handler = new ProbeActorDetail(players);

        await handler.RunAsync(session, Req1307(me.EntityId, away.EntityId), me);
        Assert.Equal(away.EntityId, ReqActorDetailInfoHandler.SelectedTargetOf(me));
        await handler.RunAsync(session, Req1307(me.EntityId, loading.EntityId), me);
        await handler.RunAsync(session, Req1307(me.EntityId, near.EntityId), me);

        
        var (op, body) = await ReadRmiFrameAsync(client);
        Assert.Equal(ZoneS2COpcodes.AnsActorIconInfo, op);
        Assert.Equal(Convert.ToHexString(ReqActorDetailInfoHandler.BuildIconOf(near)), Convert.ToHexString(body));
        Assert.Equal(near.EntityId, BinaryPrimitives.ReadUInt32LittleEndian(body));
        Assert.Equal(0, client.Available);
    }

    private static byte[] Req1307(uint self, uint target)
    {
        var b = Convert.FromHexString("010c0c00cee2" + "0000000000000000");
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(6), self);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(10), target);
        return b;
    }

    
 /// <summary>
 /// Refusal body for 1420: an 81-byte 2556 with the account and result 1018, sent with no 2938 or 2505 before it.
 /// Bytes 6..71 are zero. Bytes 72..80 differ between refusals and are sent as zero.
 /// </summary>
    [Fact]
    public void Refusal2556_MatchesRetailsRefusal_UpToTheUninitialisedTail()
    {
        var retail = Convert.FromHexString(
            "c1244a00fa03" + new string('0', 66 * 2) + "32334d0f0000000004");
        Assert.Equal(81, retail.Length);
        var ours = ChannelMoveHandler.BuildRefusal2556(0x004A24C1, ChannelMoveHandler.ResultCurrentChannel);
        Assert.Equal(81, ours.Length);
        Assert.Equal(Convert.ToHexString(retail.AsSpan(0, 72)), Convert.ToHexString(ours.AsSpan(0, 72)));
        Assert.All(ours.AsSpan(72).ToArray(), b => Assert.Equal(0, b));
    }

 /// <summary>The success body of the 2556 answer to 1420, rebuilt from its own fields.</summary>
    [Fact]
    public void Success2556_RebuildsRetailsFrame()
    {
        var retail = Convert.FromHexString(
            "24a11900000052c591456f98f846b78c15c8c0516aa03100320038002e003200340031002e00390033002e0032003100310000000000"
            + "271c00000000000000000000000000000000004cab56ab8f3c296f");
        var ours = ChannelMoveHandler.BuildAnsChannelMove2556(0x0019A124, 0, retail.AsSpan(6, 16), "128.241.93.211", 7207,
            0x6F293C8FAB56AB4CUL);
        Assert.Equal(Convert.ToHexString(retail), Convert.ToHexString(ours));
    }

    [Fact]
    public void RefusalCode_CurrentChannel1018_UnlistedHandle1014()
    {
        var world = new WorldConfig { ChannelCount = 3, ServerIndex = 1 };
        uint ch1 = ZoneChannelList.ChannelId(world, 1), ch2 = ZoneChannelList.ChannelId(world, 2);
        Assert.Equal(0x06514041u, ch1); 

        Assert.Equal(ChannelMoveHandler.ResultCurrentChannel, ChannelMoveHandler.RefusalCode(ch2, ch2, world));
        Assert.Null(ChannelMoveHandler.RefusalCode(ch2, ch1, world));
        
        Assert.Null(ChannelMoveHandler.RefusalCode(ch1, null, world));
        
        Assert.Equal(ChannelMoveHandler.ResultNoSuchChannel, ChannelMoveHandler.RefusalCode(0x06510041, ch1, world));
        Assert.Equal(ChannelMoveHandler.ResultNoSuchChannel, ChannelMoveHandler.RefusalCode(0, null, world));
        Assert.Equal(ChannelMoveHandler.ResultNoSuchChannel,
            ChannelMoveHandler.RefusalCode(ZoneChannelList.ChannelId(world, world.ChannelCount + 1), null, world));
    }

 /// <summary>
 /// Live path of a refusal: a 1420 for an unlisted account gets a single 81-byte 2556 with result 1014 and nothing else.
 /// The refusal returns before any code that needs the database or the ticket store.
 /// </summary>
    [Fact]
    public async Task ChannelMove1420_UnlistedHandle_IsALone2556_1014()
    {
        await using var net = new Loopback();
        var (session, client) = await net.ConnectAsync();
        var players = new PlayerStateManager(new NoDirtySink());
        var player = new PlayerState { SessionId = session.SessionId, EntityId = PlayerStateManager.BuildActorHandle(session.SessionId),
            AccountId = 0x0047AEA7, CharacterUid = 0x0DE0B6B3A7640001UL, MapId = 1100 };
        players.AddPlayer(session.SessionId, player);
        var services = new ServiceCollection().AddSingleton(new ServerConfiguration()).BuildServiceProvider();

        await new ProbeChannelMove(players).RunAsync(session, Convert.FromHexString("41005106"), player, services);

        var (op, body) = await ReadRmiFrameAsync(client);
        Assert.Equal(ZoneS2COpcodes.AnsChannelMove, op);
        
        Assert.Equal("A7AE4700F603" + new string('0', 75 * 2), Convert.ToHexString(body));
        Assert.Equal(0, client.Available);
    }

    
 /// <summary>
 /// Non-GM '/' chat lines get no 2324 or 2893 reply on the sender's session. The 1315 body layout is
 /// [u32 blobLen][u16 0][u16 58012][u32 handle][u8 channel][u16 msgLen][units]. A non-GM's unknown line or /pos
 /// sends nothing, so the first frame on the wire is the help line sent once the player is a GM.
 /// </summary>
    [Fact]
    public async Task SlashLine_FromANonGm_GetsNoAnswer_AGmStillGetsTheHelpLine()
    {
        await using var net = new Loopback();
        var (session, client) = await net.ConnectAsync();
        var players = new PlayerStateManager(new NoDirtySink());
        var player = new PlayerState { SessionId = session.SessionId, EntityId = PlayerStateManager.BuildActorHandle(session.SessionId),
            Name = "Tester", MapId = 1100, AccessLevel = 0 };
        players.AddPlayer(session.SessionId, player);
        var chat = new ProbeChat(players);
        Assert.False(ChatHandler.AnswersSlashLine(player));

        await chat.RunAsync(session, Req1315(player.EntityId, 0, "/esacpe"), player);
        await chat.RunAsync(session, Req1315(player.EntityId, 0, "/pos"), player);

        player.AccessLevel = 1;
        Assert.True(ChatHandler.AnswersSlashLine(player));
        await chat.RunAsync(session, Req1315(player.EntityId, 0, "/pos"), player);
        await chat.RunAsync(session, Req1315(player.EntityId, 0, "/esacpe"), player);

        
        
        var (op, _) = await ReadRmiFrameAsync(client);
        Assert.Equal(ZoneS2COpcodes.AnsGmCommand, op);
        var (op2, body2) = await ReadRmiFrameAsync(client);
        Assert.Equal(ZoneS2COpcodes.OnReceiveChat, op2);
        Assert.Contains("cmds: /pos", Encoding.Unicode.GetString(body2), StringComparison.Ordinal);
    }

    private static byte[] Req1315(uint handle, byte channel, string text)
    {
        var units = Encoding.Unicode.GetBytes(text);
        var b = new byte[15 + units.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)(b.Length - 4));
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(6), 58012);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), handle);
        b[12] = channel;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(13), (ushort)text.Length);
        units.CopyTo(b, 15);
        return b;
    }

    
 /// <summary>Reads one ProudNet frame ([u16 magic][scalar len][01][u16 rmi][body]) and returns the RMI and its body.</summary>
    private static async Task<(ushort op, byte[] body)> ReadRmiFrameAsync(Socket client)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var head = await ReadExactAsync(client, 3, cts.Token);
        int width = head[2];
        var lenBytes = await ReadExactAsync(client, width, cts.Token);
        int len = width switch
        {
            1 => lenBytes[0],
            2 => BinaryPrimitives.ReadInt16LittleEndian(lenBytes),
            _ => BinaryPrimitives.ReadInt32LittleEndian(lenBytes),
        };
        var data = await ReadExactAsync(client, len, cts.Token);
        Assert.Equal(0x01, data[0]);
        return (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(1)), data[3..]);
    }

    private static async Task<byte[]> ReadExactAsync(Socket s, int n, CancellationToken ct)
    {
        var buf = new byte[n];
        int got = 0;
        while (got < n)
        {
            int r = await s.ReceiveAsync(buf.AsMemory(got), SocketFlags.None, ct);
            Assert.True(r > 0, "connection closed");
            got += r;
        }
        return buf;
    }

    private static IServiceProvider NoServices { get; } = new ServiceCollection().BuildServiceProvider();

    private sealed class ProbeMovement(PlayerStateManager pm)
        : PlayerMovementHandler(null!, NullLogger<PlayerMovementHandler>.Instance, pm, new BroadcastService(pm, NullLogger<BroadcastService>.Instance))
    {
        public Task RunAsync(ProudSession s, byte[] body, PlayerState p) => HandleAsync(s, body, p, NoServices, CancellationToken.None);
    }

    private sealed class ProbeActorDetail(PlayerStateManager pm)
        : ReqActorDetailInfoHandler(null!, NullLogger<ReqActorDetailInfoHandler>.Instance, pm, new BroadcastService(pm, NullLogger<BroadcastService>.Instance))
    {
        public Task RunAsync(ProudSession s, byte[] body, PlayerState p) => HandleAsync(s, body, p, NoServices, CancellationToken.None);
    }

    private sealed class ProbeChannelMove(PlayerStateManager pm)
        : ChannelMoveHandler(null!, NullLogger<ChannelMoveHandler>.Instance, pm, new BroadcastService(pm, NullLogger<BroadcastService>.Instance))
    {
        public Task RunAsync(ProudSession s, byte[] body, PlayerState p, IServiceProvider services)
            => HandleAsync(s, body, p, services, CancellationToken.None);
    }

    private sealed class ProbeChat(PlayerStateManager pm)
        : ChatHandler(null!, NullLogger<ChatHandler>.Instance, pm, new BroadcastService(pm, NullLogger<BroadcastService>.Instance))
    {
        public Task RunAsync(ProudSession s, byte[] body, PlayerState p) => HandleAsync(s, body, p, NoServices, CancellationToken.None);
    }

 /// <summary>Real connected <see cref="ProudSession"/>s on the accepting end of loopback sockets.</summary>
    private sealed class Loopback : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly List<Socket> _clients = [];
        private readonly List<ProudSession> _sessions = [];
        private uint _nextId = 0xA6000;

        public Loopback() => _listener.Start();

        public async Task<(ProudSession session, Socket client)> ConnectAsync()
        {
            var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            var accept = _listener.AcceptSocketAsync();
            await client.ConnectAsync((IPEndPoint)_listener.LocalEndpoint);
            var accepted = await accept;
            var session = new ProudSession(accepted, ++_nextId, NullLogger.Instance);
            _clients.Add(client);
            _sessions.Add(session);
            return (session, client);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var s in _sessions) await s.DisposeAsync();
            foreach (var c in _clients) c.Dispose();
            _listener.Stop();
        }
    }
}
