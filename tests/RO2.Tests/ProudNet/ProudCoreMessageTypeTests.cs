using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Network.Handlers;
using RO2.Network.Handlers.Shared;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Extensions;
using Xunit;

namespace RO2.Tests.ProudNet;

/// <summary>
/// The ProudNet message-type table (<see cref="ProudCoreOpCode"/>) and the two consequences this server
/// relies on.
///
/// <para>Each member is the one-byte type tag of a ProudNet core message. The tests below pin that table
/// and what this server does with it today.</para>
/// </summary>
public sealed class ProudCoreMessageTypeTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);
    private static readonly byte[] Rc4Key = Enumerable.Range(0, 64).Select(i => (byte)(i * 29 + 7)).ToArray();

    /// <summary>(member, value, note).</summary>
    public static TheoryData<ProudCoreOpCode, byte, string> BinaryTable => new()
    {
 { ProudCoreOpCode.Rmi, 0x01, "server case 1 IoCompletion_ProcessMessage_Rmi; client case 1" },
 { ProudCoreOpCode.UserMessage, 0x02, "server case 2; SendUserMessage pushes 2" },
 { ProudCoreOpCode.ConnectServerTimedout, 0x03, "PurgeTooOldUnmatureClient pushes 3" },
 { ProudCoreOpCode.NotifyServerConnectionHint, 0x04, "RequestServerConnectionHint handler pushes 4" },
 { ProudCoreOpCode.EncryptedSessionKey, 0x05, "client pushes 5; server case 5" },
 { ProudCoreOpCode.SessionKeySuccess, 0x06, "server case-5 handler pushes 6" },
 { ProudCoreOpCode.ClientConnectInfo, 0x07, "pushes 7; server case 7" },
 { ProudCoreOpCode.NotifyProtocolVersionMismatch, 0x08, "SendCoreMsg8 pushes 8; client raises error 8" },
 { ProudCoreOpCode.NotifyServerDeniedConnection, 0x09, "pushes 9" },
 { ProudCoreOpCode.ConnectSuccess, 0x0A, "AssignHostIDAndNotifyConnectSuccess pushes 0Ah" },
 { ProudCoreOpCode.RequestStartServerHolepunch, 0x0B, "IssueUdpMagicNumberAndNotify pushes 0Bh" },
 { ProudCoreOpCode.ServerHolepunch, 0x0C, "pushes 0Ch; server case 0Ch" },
 { ProudCoreOpCode.ServerHolepunchAck, 0x0D, "server ServerHolepunch handler pushes 0Dh" },
 { ProudCoreOpCode.NotifyHolepunchSuccess, 0x0E, "pushes 0Eh; server case 0Eh" },
 { ProudCoreOpCode.NotifyClientServerUdpMatched, 0x0F, "server NotifyHolepunchSuccess handler pushes 0Fh" },
 { ProudCoreOpCode.PeerUdp_ServerHolepunch, 0x10, "pushes 10h; server case 10h" },
 { ProudCoreOpCode.PeerUdp_ServerHolepunchAck, 0x11, "server pushes 11h" },
 { ProudCoreOpCode.PeerUdp_NotifyHolepunchSuccess, 0x12, "pushes 12h; server case 12h" },
 { ProudCoreOpCode.ReliableRelay1, 0x14, "Send_BroadcastLayer pushes 14h; server case 14h" },
 { ProudCoreOpCode.UnreliableRelay1, 0x15, "Send_BroadcastLayer pushes 15h; server case 15h" },
 { ProudCoreOpCode.UnreliableRelay1_RelayDestListCompressed, 0x16, "pushes 16h; server case 16h" },
 { ProudCoreOpCode.LingerDataFrame1, 0x17, "CRemotePeerReliableUdp::vf1 pushes 17h; server case 17h" },
 { ProudCoreOpCode.ReliableRelay2, 0x18, "server ReliableRelay1 handler pushes 18h" },
 { ProudCoreOpCode.UnreliableRelay2, 0x19, "MulticastUnreliableRelay2 pushes 19h" },
 { ProudCoreOpCode.LingerDataFrame2, 0x1A, "server LingerDataFrame1 handler pushes 1Ah" },
 { ProudCoreOpCode.UnreliablePing, 0x1B, "pushes 1Bh; server case 1Bh IoCompletion_ProcessMessage_UnreliablePing" },
 { ProudCoreOpCode.SpeedHackDetectorPing, 0x1C, "pushes 1Ch; server case 1Ch" },
 { ProudCoreOpCode.UnreliablePong, 0x1D, "server UnreliablePing handler pushes 1Dh; client case 1Dh" },
 { ProudCoreOpCode.ServerKeepAliveTouch, 0x1E, "Heartbeat_SendUnreliablePing pushes 1Eh; client case 1Eh is a no-op" },
 { ProudCoreOpCode.PeerUdp_PeerHolepunch, 0x1F, "pushes 1Fh; client case 1Fh" },
 { ProudCoreOpCode.PeerUdp_PeerHolepunchAck, 0x20, "pushes 20h; client case 20h" },
 { ProudCoreOpCode.P2PUnreliablePing, 0x21, "pushes 21h; client case 21h answers 22h" },
 { ProudCoreOpCode.P2PUnreliablePong, 0x22, "pushes 22h; client case 22h" },
 { ProudCoreOpCode.S2CRoutedMulticast1, 0x23, "server Send_BroadcastLayer pushes 23h; client case 23h" },
 { ProudCoreOpCode.S2CRoutedMulticast2, 0x24, "pushes 24h (to peers); client case 24h; no server case" },
 { ProudCoreOpCode.EncryptedReliable, 0x25, "Send_SecureLayer; ProcessMessage_Encrypted checks the counter only for 37" },
 { ProudCoreOpCode.EncryptedUnreliable, 0x26, "Send_SecureLayer pushes 26h" },
 { ProudCoreOpCode.Compressed, 0x27, "Send_CompressLayer pushes 27h" },
 { ProudCoreOpCode.RequestReceiveSpeedAtReceiverSide_NoRelay, 0x28, "vf2_off188 pushes 28h; server case 28h" },
 { ProudCoreOpCode.ReplyReceiveSpeedAtReceiverSide_NoRelay, 0x29, "server pushes 29h; client case 29h" },
 { ProudCoreOpCode.RequestServerConnectionHint, 0x2F, "pushes 2Fh; server case 2Fh" },
 { ProudCoreOpCode.P2PReliablePing, 0x31, "pushes 31h; client case 31h answers 32h" },
 { ProudCoreOpCode.P2PReliablePong, 0x32, "pushes 32h; client case 32h" },
    };

    [Theory]
    [MemberData(nameof(BinaryTable))]
    public void MessageType_HasTheBinarysValue(ProudCoreOpCode type, byte expected, string evidence)
    {
        Assert.True(expected == (byte)type, $"{type}: expected 0x{expected:X2} ({evidence})");
    }

    /// <summary>
    /// The ping handler base is bound to the real ping, 0x1B, and the reliable one stays on 0x24, a
    /// value no client sends a server, where it does nothing.
    /// </summary>
    [Fact]
    public async Task PingHandlers_AreBoundToTheBinarysValues()
    {
        var unreliable = new UnreliableProbe(NullLogger<UnreliableProbe>.Instance);
        var reliable = new ReliableProbe(NullLogger<ReliableProbe>.Instance);

        Assert.Equal(0x1B, (byte)unreliable.Opcode);
        Assert.Equal(ProudCoreOpCode.UnreliablePing, unreliable.Opcode);
        Assert.Equal(0x24, (byte)reliable.Opcode);

        // The reliable one answers nothing: the next frame on the wire is the pong to a real ping.
        await using var h = await Harness.StartAsync();
        await reliable.HandleAsync(h.Session, new byte[] { 0x00 });
        await unreliable.HandleAsync(h.Session, ClientPing(clientTime: 7, smoothedPing: 0).AsMemory(1));
        var frame = await h.ReadS2CFrameAsync();
        Assert.Equal((byte)ProudCoreOpCode.UnreliablePong, frame[4]);
    }

    /// <summary>
    /// A 0x26 goes to the decrypt path and comes out as the RMI it carries. It is never raised as a
    /// core message, which is why the ping handler, bound to 0x26 under its old name, never ran. A 0x1B
    /// sent right after it is the first core message the session raises. The 0x26 carries 1371
    /// ReqPCTeleport, the counter-less frame ProudCompressedFrameTests uses.
    /// </summary>
    [Fact]
    public async Task Encrypted0x26_NeverReachesTheCoreHandlers()
    {
        await using var h = await Harness.StartAsync();
        await h.SendAsync(EncryptedUnreliable(Convert.FromHexString("015b0583050000")));
        await h.SendAsync(ProudFrameCodec.EncodeFrame(ClientPing(clientTime: 1, smoothedPing: 2)));

        var (rmiId, body) = await h.NextPacketAsync();
        Assert.Equal(1371, rmiId);
        Assert.Equal(new byte[] { 0x83, 0x05, 0x00, 0x00 }, body);

        var (type, _) = await h.NextCoreAsync();
        Assert.Equal(ProudCoreOpCode.UnreliablePing, type);
    }

    /// <summary>
    /// The client's ping reaches the core handlers as 0x1B with its 12-byte body. The layout is
    /// <c>[0x1B][u64 clientTime][u32 smoothed ping]</c>, read as 8 bytes and then 4. The values are
    /// placeholders.
    /// </summary>
    [Fact]
    public async Task UnreliablePing0x1B_ReachesTheCoreHandlersWithItsTwelveByteBody()
    {
        await using var h = await Harness.StartAsync();
        await h.SendAsync(ProudFrameCodec.EncodeFrame(ClientPing(clientTime: 0x0102030405060708, smoothedPing: 45)));

        var (type, payload) = await h.NextCoreAsync();
        Assert.Equal(ProudCoreOpCode.UnreliablePing, type);
        Assert.Equal(12, payload.Length);
        Assert.Equal(0x0102030405060708UL, BinaryPrimitives.ReadUInt64LittleEndian(payload));
        Assert.Equal(45u, BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(8)));
    }

    /// <summary>
    /// The pong body is <c>[u64 echoed clientTime][u64 serverTime]</c>, 16 bytes, which is what the
    /// client reads as two 8-byte values. Only the first 8 ping bytes are echoed; the client's u32 ping
    /// is not.
    /// </summary>
    [Fact]
    public void UnreliablePong_EchoesTheClientTimeAndAddsTheServerClock()
    {
        var ping = ClientPing(clientTime: 0x1122334455667788, smoothedPing: 0x99).AsSpan(1);

        var pong = UnreliablePongMessage.Build(ping, serverTimeMs: 0x0A0B0C0D0E0F1011);

        Assert.NotNull(pong);
        Assert.Equal(Convert.FromHexString("8877665544332211" + "11100F0E0D0C0B0A"), pong);
        Assert.Equal(UnreliablePongMessage.BodyBytes, pong.Length);
    }

    /// <summary>
    /// The server answers only when both reads succeed (8 then 4 bytes), so a ping shorter than 12
    /// bytes gets no pong; a longer one is read up to its 12th byte.
    /// </summary>
    [Fact]
    public void UnreliablePong_IsNotBuiltForAPingTooShortForRetailsReads()
    {
        var ping = ClientPing(clientTime: 5, smoothedPing: 6)[1..];

        Assert.Null(UnreliablePongMessage.Build(ping.AsSpan(0, 11), 1));
        Assert.Null(UnreliablePongMessage.Build([], 1));
        Assert.Equal(UnreliablePongMessage.Build(ping, 1), UnreliablePongMessage.Build([.. ping, 0xEE, 0xEE], 1));
    }

    /// <summary>
    /// The server clock is milliseconds on a monotonic counter: two reads a known delay apart differ by
    /// at least most of that delay and never go backwards.
    /// </summary>
    [Fact]
    public async Task ServerTime_IsAMonotonicMillisecondClock()
    {
        long first = UnreliablePongMessage.ServerTimeMs();
        await Task.Delay(50);
        long second = UnreliablePongMessage.ServerTimeMs();

        Assert.InRange(second - first, 40, 60_000);
    }

    /// <summary>
    /// What the ping handler puts on the wire: <c>[13 57][01 11][1D][echoed clientTime][serverTime]</c>,
    /// with the server time read from <see cref="UnreliablePongMessage.ServerTimeMs"/> while it ran.
    /// A too-short ping before it is not answered, so the first frame is this pong.
    /// </summary>
    [Fact]
    public async Task UnreliablePing_IsAnsweredWithRetailsPong()
    {
        await using var h = await Harness.StartAsync();
        var handler = new UnreliableProbe(NullLogger<UnreliableProbe>.Instance);
        var ping = ClientPing(clientTime: 0x1122334455667788, smoothedPing: 0x99);

        await handler.HandleAsync(h.Session, ping.AsMemory(1, 8)); // too short: no answer
        long before = UnreliablePongMessage.ServerTimeMs();
        await handler.HandleAsync(h.Session, ping.AsMemory(1));
        long after = UnreliablePongMessage.ServerTimeMs();

        var frame = await h.ReadS2CFrameAsync();
        Assert.Equal(Convert.FromHexString("13570111" + "1D" + "8877665544332211"), frame[..13]);
        Assert.Equal(4 + 1 + UnreliablePongMessage.BodyBytes, frame.Length);
        Assert.InRange(BinaryPrimitives.ReadInt64LittleEndian(frame.AsSpan(13)), before, after);
    }

    /// <summary>
    /// Through Zone's production wiring (ZoneHandlerServiceExtensions.WireZoneHandlers) and a real
    /// ProudServer, with Zone's own UnreliablePingHandler and HeartbeatHandler both registered: a 0x1B
    /// on the wire is answered with the 16-byte pong, so the single-opcode ping handler is the one that
    /// runs, whatever the multi-opcode heartbeat handler lists.
    /// </summary>
    [Fact]
    public async Task ZoneWiring_RoutesTheRealPingToThePingHandler()
    {
        await using var provider = new ServiceCollection()
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .AddSingleton<IZoneCoreHandler, RO2.Server.Zone.Handlers.Core.UnreliablePingHandler>()
            .AddSingleton<IZoneMultiCoreHandler, RO2.Server.Zone.Handlers.Core.HeartbeatHandler>()
            .BuildServiceProvider();
        await using var server = new ProudServer("Test", NullLogger.Instance);
        ZoneHandlerServiceExtensions.WireZoneHandlers(provider, new RmiDispatcher(NullLogger.Instance), server,
            NullLogger.Instance);
        await server.StartAsync(new IPEndPoint(IPAddress.Loopback, 0));
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(server.LocalEndPoint!);

        var ping = ClientPing(clientTime: 0x0102030405060708, smoothedPing: 3);
        await client.SendAsync(ProudFrameCodec.EncodeFrame(ping), SocketFlags.None);

        var frame = await ReadS2CFrameAsync(client);
        Assert.Equal(Convert.FromHexString("13570111" + "1D" + "0807060504030201"), frame[..13]);
        Assert.Equal(4 + 1 + UnreliablePongMessage.BodyBytes, frame.Length);
    }

    /// <summary>
    /// The two send paths that used the renamed members still write the same type byte:
    /// SendGameRmiAsync writes 0x25 (was <c>ProudCoreOpCode.Rmi</c>, now <see cref="ProudCoreOpCode.EncryptedReliable"/>)
    /// and SendInternalRmiAsync writes 0x01 (was <c>InternalRmi</c>, now <see cref="ProudCoreOpCode.Rmi"/>).
    /// </summary>
    [Fact]
    public async Task SendPaths_WriteTheSameTypeBytesAsBefore()
    {
        await using var h = await Harness.StartAsync();

        await h.Session.SendGameRmiAsync(new byte[] { 0xE1, 0x2E, 0x10 });
        Assert.Equal(Convert.FromHexString("13570107" + "25000003" + "E12E10"), await h.ReadS2CFrameAsync());

        await h.Session.SendInternalRmiAsync(1108, new byte[] { 0xAA });
        Assert.Equal(Convert.FromHexString("13570104" + "015404" + "AA"), await h.ReadS2CFrameAsync());
    }

    /// <summary>
    /// 0x41 / 0x42 (once named EncryptedRmi / CompressedRmi) are not defined message types.
    /// They are no longer set apart with their own warning. They go to the core handlers like any other
    /// type, and no handler takes them, so they still get no answer.
    /// </summary>
    [Fact]
    public async Task FormerPseudoWrappers_AreOrdinaryUnhandledCoreMessages()
    {
        await using var h = await Harness.StartAsync();
        await h.SendAsync(ProudFrameCodec.EncodeFrame(new byte[] { 0x41, 0x00 }));

        var (type, payload) = await h.NextCoreAsync();
        Assert.Equal(0x41, (byte)type);
        Assert.Equal(new byte[] { 0x00 }, payload);
    }

    // ------------------------------------------------------------------------------------------

    private static byte[] ClientPing(ulong clientTime, uint smoothedPing)
    {
        var m = new byte[13];
        m[0] = (byte)ProudCoreOpCode.UnreliablePing;
        BinaryPrimitives.WriteUInt64LittleEndian(m.AsSpan(1), clientTime);
        BinaryPrimitives.WriteUInt32LittleEndian(m.AsSpan(9), smoothedPing);
        return m;
    }

    /// <summary>[0x26][02 = RC4][CompactLen n][RC4(plaintext)], framed.</summary>
    private static byte[] EncryptedUnreliable(byte[] plaintext)
    {
        using var crypto = new ProudNetCrypto();
        crypto.SetRc4Key(Rc4Key);
        var cipher = crypto.Rc4Decrypt(plaintext)!; // symmetric: re-keyed per message
        Assert.True(cipher.Length < 128);
        return ProudFrameCodec.EncodeFrame([0x26, 0x02, 0x01, (byte)cipher.Length, .. cipher]);
    }

    private sealed class UnreliableProbe(ILogger<UnreliableProbe> logger)
        : UnreliablePingHandlerBase<UnreliableProbe>(logger);

    private sealed class ReliableProbe(ILogger<ReliableProbe> logger)
        : ReliablePingHandlerBase<ReliableProbe>(logger);

    /// <summary>One whole S2C frame read off <paramref name="socket"/>, header included:
    /// [13 57][u8 width][width bytes length][data].</summary>
    private static async Task<byte[]> ReadS2CFrameAsync(Socket socket)
    {
        var head = await ReadExactAsync(socket, 3);
        var lengthBytes = await ReadExactAsync(socket, head[2]);
        int length = head[2] switch
        {
            1 => lengthBytes[0],
            2 => BinaryPrimitives.ReadUInt16LittleEndian(lengthBytes),
            _ => BinaryPrimitives.ReadInt32LittleEndian(lengthBytes),
        };
        var data = await ReadExactAsync(socket, length);
        return [.. head, .. lengthBytes, .. data];
    }

    private static async Task<byte[]> ReadExactAsync(Socket socket, int count)
    {
        var buffer = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = await socket.ReceiveAsync(buffer.AsMemory(read), SocketFlags.None)
                .AsTask().WaitAsync(Generous);
            if (n == 0)
                throw new EndOfStreamException("connection closed mid-frame");
            read += n;
        }
        return buffer;
    }

    /// <summary>A real <see cref="ProudSession"/> on the accepting end of a loopback connection.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly Socket _client;
        private readonly Channel<(ushort, byte[])> _packets = Channel.CreateUnbounded<(ushort, byte[])>();
        private readonly Channel<(ProudCoreOpCode, byte[])> _core = Channel.CreateUnbounded<(ProudCoreOpCode, byte[])>();

        public ProudSession Session { get; }

        private Harness(TcpListener listener, Socket client, Socket accepted)
        {
            _listener = listener;
            _client = client;
            Session = new ProudSession(accepted, 9, NullLogger.Instance);
            Session.Crypto.SetRc4Key(Rc4Key);
            Session.Handshake = HandshakeState.Connected; // RMIs are dispatched only after the handshake (S-H5)
            Session.PacketReceived += (_, id, payload) =>
            {
                _packets.Writer.TryWrite((id, payload.ToArray()));
                return Task.CompletedTask;
            };
            Session.CoreMessageReceived += (_, op, payload) =>
            {
                _core.Writer.TryWrite((op, payload.ToArray()));
                return Task.CompletedTask;
            };
            Session.StartReceiving();
        }

        public static async Task<Harness> StartAsync()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            var accept = listener.AcceptSocketAsync();
            await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
            var accepted = await accept.WaitAsync(Generous);
            return new Harness(listener, client, accepted);
        }

        public async Task SendAsync(byte[] frame) => await _client.SendAsync(frame, SocketFlags.None);

        public async Task<(ushort RmiId, byte[] Body)> NextPacketAsync()
            => await _packets.Reader.ReadAsync().AsTask().WaitAsync(Generous);

        public async Task<(ProudCoreOpCode Type, byte[] Payload)> NextCoreAsync()
            => await _core.Reader.ReadAsync().AsTask().WaitAsync(Generous);

        /// <summary>One whole S2C frame, header included (see the outer ReadS2CFrameAsync).</summary>
        public Task<byte[]> ReadS2CFrameAsync() => ProudCoreMessageTypeTests.ReadS2CFrameAsync(_client);

        public async ValueTask DisposeAsync()
        {
            await Session.DisposeAsync();
            _client.Dispose();
            _listener.Stop();
        }
    }
}
