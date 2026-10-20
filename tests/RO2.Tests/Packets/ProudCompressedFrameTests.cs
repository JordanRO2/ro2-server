using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Network.ProudNet;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// ProudNet compressed messages (type 0x27) and the counter-less encrypted wrapper (0x26) they arrive in,
/// driven through a real <see cref="ProudSession"/> over a loopback socket.
///
/// Every payload below is a client-to-server plaintext frame of RMI 1316 ReqPCWhisper, RC4-encrypted and
/// compressed. Two facts pin their wire shape:
///
///  1. Their plaintext starts AT the 0x27, with no u16 encrypt counter in front, and they consume no counter
///     value: the counter-bearing frames before and after one on the same connection carry consecutive
///     counters. Counter-less 0x01 RMIs behave the same.
///  2. The secure layer writes the counter only for a reliable send, tagged 37 (0x25), and tags every other
///     send 38 (0x26) with no counter. The receiver reads and checks the counter only for 37. Both
///     dispatchers send 0x27 to the decompressor and re-dispatch its output by its first byte. The
///     decompressor refuses an inflatedLen above the max message length and anything but an exact-size
///     inflate.
///
/// So a compressed whisper reaches the server as [0x26][02][CompactLen n][RC4(plaintext)]. The tests encrypt
/// the plaintext with a test key (ProudNet's RC4 re-keys per message, so the transform is its own inverse),
/// frame it, and check what the session dispatches.
///
/// Controls: a plain 0x25 1316 (counter 64) must dispatch exactly as before, and malformed wrappers must be
/// dropped with a log line instead of dispatched.
/// </summary>
public sealed class ProudCompressedFrameTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);

    /// <summary>Any 64-byte key (512-bit RC4, as the handshake installs).</summary>
    private static readonly byte[] Rc4Key = Enumerable.Range(0, 64).Select(i => (byte)(i * 37 + 11)).ToArray();

    private const ushort ReqPCWhisper = 1316;
    private const ushort ReqPCTeleport = 1371;

    // A plain 1316 "a" to CEltyHime with counter 64 (0x0040), RC4 key 8: [40 00][01][24 05][body 37 bytes].
    private const string PlainWhisperWithCounter =
        "40000124052100000000009ee211b95c0a09430045006c0074007900480069006d0065000200610001f8";

    // A counter-less 0x01 RMI (so sent as 0x26): 1371 ReqPCTeleport, body 83 05 00 00.
    private const string CounterlessTeleport = "015b0583050000";

    public static TheoryData<string, string, int, string, ushort> RetailCompressedWhispers => new()
    {
        // plaintext, frame label, inflatedLen, target name, the id the old fixed-offset read (LE16 of plaintext
        // bytes 3..4, i.e. the 0x27 header) would have dispatched it under
        {
            "270131021a01789c6354611566640082798f04ab5bd85993184a195280b092a19ec186c188c1140895191c4718b46360fc01008528261c",
            "02d06ec2-02c73c99-0000af78 05:21:55.033 (x1)", 282, "buddy", 6658
        },
        {
            "270139022201789c6354619566640082798f0477c670713a33a432e4309430543278306432e40279f50c360c460ca640a8cce038c2a01d03e30f0046a027ad",
            "f560bfac-7c82a2ad-00003c94 00:17:32.775 (x11)", 290, "CeltyHime", 8706
        },
        {
            "27013d022801789c6354615564640082798f04af72f3f0a4321433943014312432a43080d8194056114336433d830d83118329102a33388e3068c7c0f80300c6992900",
            "c9f4656e-00f32481-00003544 11:32:07.071 (x11)", 296, "estradeshark", 10242
        },
        {
            "27013f022a01789c6354615566640082798f04af72f3f0a6321433943014312432a43080d81940561143368335433d830d83118329102a33388e3068c7c0f803001535293e",
            "c9f4656e-00f32481-00003544 11:43:40.006 (x1)", 298, "estradeshark;", 10754
        },
        {
            "270133021c01789c6354611565640082798f04af72f3b0f9333831a43214016125433d830d83118329102a33388e3068c7c0f80300b06c2640",
            "c9f4656e-00f32481-00003544 11:50:12.109 (x15)", 284, "OBerry", 7170
        },
    };

    /// <summary>
    /// The fix, on real compressed frames: each compressed whisper, inside the
    /// counter-less RC4 wrapper it was sent in, reaches the RMI dispatcher as a normal 1316 with
    /// the body an uncompressed 1316 has. The body is checked against an independent inflate
    /// (ZLibStream, not the session's ZLibDecoder) and against the WhisperHandler layout, which it
    /// must consume to the last byte.
    /// </summary>
    [Theory]
    [MemberData(nameof(RetailCompressedWhispers))]
    public async Task RetailCompressedWhisper_InCounterlessRc4_DispatchesTheInner1316(
        string plaintextHex, string capturedAt, int inflatedLen, string target, ushort oldGarbageId)
    {
        var plaintext = Convert.FromHexString(plaintextHex);
        var inflated = InflateIndependently(plaintext);
        Assert.Equal(inflatedLen, inflated.Length);
        Assert.Equal(new byte[] { 0x01, 0x24, 0x05 }, inflated[..3]); // [RMI][u16 1316]
        Assert.Equal(oldGarbageId, BinaryPrimitives.ReadUInt16LittleEndian(plaintext.AsSpan(3)));

        await using var h = await Harness.StartAsync();
        await h.SendAsync(Encrypted(MessageTypeCounterless, plaintext));

        var (rmiId, body) = await h.NextPacketAsync();
        Assert.True(rmiId == ReqPCWhisper, $"{capturedAt}: dispatched as {rmiId}, not 1316 (old code: {oldGarbageId})");
        Assert.Equal(inflated[3..], body);

        // WhisperHandler's view of it: u32 blobLen, envelope (innerSize 0, msgTag 58014), sender
        // handle (PC-tagged 0x11), u8 targetChars + name, u16 msgChars + message incl. U+F801.
        Assert.Equal(body.Length - 4, BinaryPrimitives.ReadInt32LittleEndian(body));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(4)));
        Assert.Equal(58014, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(6)));
        Assert.Equal(0x11, body[8]);
        int targetChars = body[12];
        Assert.Equal(target, Encoding.Unicode.GetString(body, 13, targetChars * 2));
        int p = 13 + targetChars * 2;
        int msgChars = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(p));
        Assert.Equal(127, msgChars);
        Assert.Equal(body.Length, p + 2 + msgChars * 2);
        Assert.Equal("<255#" + new string('A', 120) + ">", Encoding.Unicode.GetString(body, p + 2, (msgChars - 1) * 2));
        Assert.Equal(0xF801, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(body.Length - 2)));
    }

    /// <summary>
    /// End to end through the real WhisperHandler: the retail compressed whisper to CeltyHime,
    /// sent in its counter-less RC4 wrapper to a real ProudServer, is delivered as 2325 to the
    /// target and echoed to the sender with the full 126-character message. Before the fix the
    /// handler never ran for it.
    /// </summary>
    [Fact]
    public async Task RetailCompressedWhisper_ReachesWhisperHandler_AndIsDelivered()
    {
        var plaintext = Convert.FromHexString((string)RetailCompressedWhispers.ElementAt(1)[0]); // to CeltyHime
        const uint senderEntity = 0x0a5cb911, targetEntity = 0x0a5cc011;

        await using var server = new ProudServer("Test", NullLogger.Instance);
        await server.StartAsync(new IPEndPoint(IPAddress.Loopback, 0));
        using var senderClient = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using var targetClient = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await senderClient.ConnectAsync(server.LocalEndPoint!);
        await targetClient.ConnectAsync(server.LocalEndPoint!);
        var deadline = DateTime.UtcNow + Generous;
        while (server.SessionCount < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.Equal(2, server.SessionCount);

        ProudSession SessionOf(Socket c) =>
            server.GetSessions().Single(s => s.RemoteEndPoint!.Port == ((IPEndPoint)c.LocalEndPoint!).Port);
        var senderSession = SessionOf(senderClient);
        var targetSession = SessionOf(targetClient);
        senderSession.Crypto.SetRc4Key(Rc4Key);
        senderSession.Handshake = HandshakeState.Connected; // RMIs are dispatched only after the handshake (S-H5)

        var players = new RO2.Server.Zone.Services.PlayerStateManager(new NoPersistence());
        players.AddPlayer(senderSession.SessionId, new RO2.Server.Zone.Models.PlayerState
            { SessionId = senderSession.SessionId, Name = "Sender", EntityId = senderEntity });
        players.AddPlayer(targetSession.SessionId, new RO2.Server.Zone.Models.PlayerState
            { SessionId = targetSession.SessionId, Name = "CeltyHime", EntityId = targetEntity });
        var broadcast = new RO2.Server.Zone.Services.BroadcastService(players,
            NullLogger<RO2.Server.Zone.Services.BroadcastService>.Instance);
        broadcast.SetServer(server);
        var handler = new RO2.Server.Zone.Handlers.Rmi.WhisperHandler(
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<RO2.Server.Zone.Handlers.Rmi.WhisperHandler>.Instance, players, broadcast);
        server.PacketReceived += (s, id, payload) => id == handler.Opcode ? handler.HandleAsync(s, payload) : Task.CompletedTask;

        await senderClient.SendAsync(Encrypted(MessageTypeCounterless, plaintext), SocketFlags.None);

        var message = "<255#" + new string('A', 120) + ">";
        var messageRaw = Encoding.Unicode.GetBytes(message + '');
        foreach (var (client, owner) in new[] { (targetClient, targetEntity), (senderClient, senderEntity) })
        {
            var (rmiId, body) = await ReadInternalRmiAsync(client);
            Assert.Equal(2325, rmiId);
            Assert.Equal(RO2.Server.Zone.Handlers.Rmi.WhisperHandler.BuildWhisperBlob(58015u << 16, owner,
                Encoding.Unicode.GetBytes("Sender"), Encoding.Unicode.GetBytes("CeltyHime"), messageRaw), body);
            // Read back independently: [u32 blobLen][11-byte head][u8 6 "Sender"][u8 9 "CeltyHime"][u16 127 msg].
            int p = 4 + 11 + 1 + 12 + 1 + 18;
            Assert.Equal(127, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(p)));
            Assert.Equal(message, Encoding.Unicode.GetString(body, p + 2, 126 * 2));
        }
    }

    /// <summary>
    /// A reliable compressed send: the compress layer runs first, so the 0x27 message sits right
    /// after the u16 counter of a 0x25. Compressed whispers normally arrive as 0x26, so this is the same
    /// message behind a counter, the shape the secure layer builds for a reliable send.
    /// </summary>
    [Fact]
    public async Task CompressedWhisper_BehindThe0x25Counter_DispatchesTheSame1316()
    {
        var plaintext = Convert.FromHexString((string)RetailCompressedWhispers.First()[0]);
        var withCounter = new byte[2 + plaintext.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(withCounter, 41);
        plaintext.CopyTo(withCounter, 2);

        await using var h = await Harness.StartAsync();
        await h.SendAsync(Encrypted(MessageTypeWithCounter, withCounter));

        var (rmiId, body) = await h.NextPacketAsync();
        Assert.Equal(ReqPCWhisper, rmiId);
        Assert.Equal(InflateIndependently(plaintext)[3..], body);
    }

    /// <summary>
    /// Compressed but not encrypted (the secure layer skips encryption for a context with no
    /// encrypt mode): it is routed as the same message would be uncompressed, i.e. a plaintext
    /// 0x01 goes to the core handlers as InternalRmi with [u16 rmiId][body].
    /// </summary>
    [Fact]
    public async Task BareCompressedMessage_IsRoutedLikeTheUncompressedMessage()
    {
        var plaintext = Convert.FromHexString((string)RetailCompressedWhispers.First()[0]);

        await using var h = await Harness.StartAsync();
        await h.SendAsync(ProudFrameCodec.EncodeFrame(plaintext));

        var (opcode, payload) = await h.NextCoreAsync();
        Assert.Equal(ProudCoreOpCode.InternalRmi, opcode);
        Assert.Equal(InflateIndependently(plaintext)[1..], payload);
    }

    /// <summary>
    /// The counter-less wrapper also carries plain RMIs (e.g. 1371 ReqPCTeleport).
    /// They used to go to the "UnreliablePing" handler, which echoed the ciphertext back as 0x37.
    /// </summary>
    [Fact]
    public async Task RetailCounterlessRmi_In0x26_IsDispatched()
    {
        await using var h = await Harness.StartAsync();
        await h.SendAsync(Encrypted(MessageTypeCounterless, Convert.FromHexString(CounterlessTeleport)));

        var (rmiId, body) = await h.NextPacketAsync();
        Assert.Equal(ReqPCTeleport, rmiId);
        Assert.Equal(new byte[] { 0x83, 0x05, 0x00, 0x00 }, body);
    }

    /// <summary>Regression: an ordinary counter-bearing 0x25 dispatches exactly as it always did.</summary>
    [Fact]
    public async Task PlainCounterBearing0x25_DispatchesAsBefore()
    {
        var plaintext = Convert.FromHexString(PlainWhisperWithCounter);

        await using var h = await Harness.StartAsync();
        await h.SendAsync(Encrypted(MessageTypeWithCounter, plaintext));

        var (rmiId, body) = await h.NextPacketAsync();
        Assert.Equal(ReqPCWhisper, rmiId);
        Assert.Equal(plaintext[5..], body);
    }

    public static TheoryData<string, string> MalformedCompressedMessages => new()
    {
        // Each is the first compressed frame with one thing broken; the reason is the logged one.
        { "inflatedLen one short (281): the stream does not fit", "270131021901" + Zlib0Hex },
        { "inflatedLen one long (283): the stream ends early", "270131021b01" + Zlib0Hex },
        { "inflatedLen over the 64 KiB cap", "27013104" + "01000100" + Zlib0Hex },
        { "negative inflatedLen", "270131" + "02ffff" + Zlib0Hex },
        { "deflatedLen past the end of the message", "270132021a01" + Zlib0Hex },
        { "CompactLen width 3 does not exist", "270331000002" + "1a01" + Zlib0Hex },
        { "corrupt Adler-32", "270131021a01" + Zlib0Hex[..^2] + "e3" },
        { "not zlib (header 00 00)", "270131021a01" + "0000" + Zlib0Hex[4..] },
        { "header cut inside inflatedLen", "27013102" },
    };

    /// <summary>The zlib stream of the first retail frame (49 bytes, 78 9c ... Adler-32).</summary>
    private const string Zlib0Hex =
        "789c6354611566640082798f04ab5bd85993184a195280b092a19ec186c188c1140895191c4718b46360fc01008528261c";

    /// <summary>
    /// A wrapper that does not inflate to exactly its declared size is dropped with a warning, never
    /// dispatched and never allowed to grow a buffer past the declaration. A sentinel sent right
    /// after proves the drop: frames are processed in order, so the first dispatch must be it.
    /// </summary>
    [Theory]
    [MemberData(nameof(MalformedCompressedMessages))]
    public async Task MalformedCompressedMessage_IsDroppedWithAWarning(string why, string plaintextHex)
    {
        var log = new CapturingLogger();
        await using var h = await Harness.StartAsync(log);
        await h.SendAsync(Encrypted(MessageTypeCounterless, Convert.FromHexString(plaintextHex)));
        await h.SendAsync(Encrypted(MessageTypeCounterless, Convert.FromHexString(CounterlessTeleport)));

        var (rmiId, _) = await h.NextPacketAsync();
        Assert.True(rmiId == ReqPCTeleport, $"{why}: the malformed message was dispatched as {rmiId}");
        Assert.Contains(log.Lines, l => l.Level == LogLevel.Warning && l.Text.Contains("Dropping compressed message (0x27)"));
    }

    /// <summary>
    /// The old bug in its general form: after decryption the RMI id was read at a fixed offset
    /// without looking at the type byte, so any message that is not an RMI went out under whatever
    /// its bytes 3-4 were. Now only RMI (0x01) and compressed (0x27) are dispatched.
    /// </summary>
    [Fact]
    public async Task DecryptedMessageOfAnotherType_IsDropped_NotDispatchedUnderAGarbageId()
    {
        // [counter 0x0005][type 0x1B][...]: read the old way this was "RMI 0x0201 = 513".
        var notAnRmi = Convert.FromHexString("05001b0102030405");

        var log = new CapturingLogger();
        await using var h = await Harness.StartAsync(log);
        await h.SendAsync(Encrypted(MessageTypeWithCounter, notAnRmi));
        await h.SendAsync(Encrypted(MessageTypeWithCounter, Convert.FromHexString(PlainWhisperWithCounter)));

        var (rmiId, _) = await h.NextPacketAsync();
        Assert.Equal(ReqPCWhisper, rmiId);
        Assert.Contains(log.Lines, l => l.Level == LogLevel.Warning
            && l.Text.Contains("Dropping decrypted message of type 0x1B"));
    }

    /// <summary>A compressed message inside a compressed message is refused (the client never nests).</summary>
    [Fact]
    public async Task NestedCompressedMessage_IsDropped()
    {
        var inner = Convert.FromHexString((string)RetailCompressedWhispers.First()[0]);
        var outer = Compress(inner);

        var log = new CapturingLogger();
        await using var h = await Harness.StartAsync(log);
        await h.SendAsync(ProudFrameCodec.EncodeFrame(outer));
        await h.SendAsync(Encrypted(MessageTypeCounterless, Convert.FromHexString(CounterlessTeleport)));

        var (rmiId, _) = await h.NextPacketAsync();
        Assert.Equal(ReqPCTeleport, rmiId);
        Assert.Contains(log.Lines, l => l.Text.Contains("nested inside another"));
    }

    /// <summary>
    /// With Debug on, the per-frame lines keep their text: the receive dump, the core-message line
    /// and the RC4 lines read as they did before they moved to generated methods. The new
    /// compressed line reports the real id.
    /// </summary>
    [Fact]
    public async Task DebugLines_KeepTheirText()
    {
        var plaintext = Convert.FromHexString((string)RetailCompressedWhispers.First()[0]);
        var frame = Encrypted(MessageTypeCounterless, plaintext);

        var log = new CapturingLogger();
        await using var h = await Harness.StartAsync(log);
        await h.SendAsync(frame);
        await h.NextPacketAsync();

        var cipherLen = plaintext.Length;
        Assert.Contains(log.Lines, l => l.Text == $">>> Len:{frame.Length} [0x26] {Convert.ToHexString(frame)}");
        Assert.Contains(log.Lines, l => l.Text == $"Session 7: Core message opcode=0x26, payload={frame.Length - 5} bytes");
        Assert.Contains(log.Lines, l => l.Text == $"Session 7: RMI flags=0x0201, dataLen={cipherLen}, actual={cipherLen}");
        Assert.Contains(log.Lines, l => l.Text == $"Session 7: RC4 decrypted data ({cipherLen} bytes): {Convert.ToHexString(plaintext)}");
        Assert.Contains(log.Lines, l => l.Text == "Session 7: Compressed message (0x27), 49 -> 282 bytes");
        Assert.Contains(log.Lines, l => l.Text == "Session 7: Compressed RMI id=1316 (0x0524), payload=279 bytes");
    }

    // ------------------------------------------------------------------------------------------

    private const byte MessageTypeWithCounter = 0x25;
    private const byte MessageTypeCounterless = 0x26;

    /// <summary>[type][02 = RC4][CompactLen n][RC4(plaintext)], framed.</summary>
    private static byte[] Encrypted(byte messageType, byte[] plaintext)
    {
        using var crypto = new ProudNetCrypto();
        crypto.SetRc4Key(Rc4Key);
        var cipher = crypto.Rc4Decrypt(plaintext)!; // symmetric: re-keyed per message

        var header = cipher.Length < 128
            ? new byte[] { messageType, 0x02, 0x01, (byte)cipher.Length }
            : new byte[] { messageType, 0x02, 0x02, (byte)cipher.Length, (byte)(cipher.Length >> 8) };
        return ProudFrameCodec.EncodeFrame([.. header, .. cipher]);
    }

    /// <summary>Inflate a 0x27 message with ZLibStream: a different API than the session uses.</summary>
    private static byte[] InflateIndependently(byte[] message)
    {
        Assert.Equal(0x27, message[0]);
        int pos = 1;
        int deflated = ReadCompactLen(message, ref pos);
        int inflated = ReadCompactLen(message, ref pos);
        Assert.Equal(message.Length - pos, deflated);

        using var z = new ZLibStream(new MemoryStream(message, pos, deflated), CompressionMode.Decompress);
        using var o = new MemoryStream();
        z.CopyTo(o);
        Assert.Equal(inflated, (int)o.Length);
        return o.ToArray();

        static int ReadCompactLen(byte[] m, ref int p)
        {
            int width = m[p];
            int v = width switch
            {
                1 => (sbyte)m[p + 1],
                2 => BinaryPrimitives.ReadInt16LittleEndian(m.AsSpan(p + 1)),
                _ => throw new InvalidDataException($"width {width}"),
            };
            p += 1 + width;
            return v;
        }
    }

    /// <summary>[0x27][CompactLen deflated][CompactLen inflated][zlib] around <paramref name="message"/>.</summary>
    private static byte[] Compress(byte[] message)
    {
        using var o = new MemoryStream();
        using (var z = new ZLibStream(o, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(message);
        var zlib = o.ToArray();
        Assert.True(zlib.Length < 128 && message.Length < 128);
        return [0x27, 0x01, (byte)zlib.Length, 0x01, (byte)message.Length, .. zlib];
    }

    /// <summary>A real <see cref="ProudSession"/> on the accepting end of a loopback connection.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly Socket _client;
        private readonly ProudSession _session;
        private readonly Channel<(ushort, byte[])> _packets = Channel.CreateUnbounded<(ushort, byte[])>();
        private readonly Channel<(ProudCoreOpCode, byte[])> _core = Channel.CreateUnbounded<(ProudCoreOpCode, byte[])>();

        private Harness(TcpListener listener, Socket client, Socket accepted, ILogger logger)
        {
            _listener = listener;
            _client = client;
            _session = new ProudSession(accepted, 7, logger);
            _session.Crypto.SetRc4Key(Rc4Key);
            _session.Handshake = HandshakeState.Connected; // RMIs are dispatched only after the handshake (S-H5)
            _session.PacketReceived += (_, id, payload) =>
            {
                _packets.Writer.TryWrite((id, payload.ToArray()));
                return Task.CompletedTask;
            };
            _session.CoreMessageReceived += (_, op, payload) =>
            {
                _core.Writer.TryWrite((op, payload.ToArray()));
                return Task.CompletedTask;
            };
            _session.StartReceiving();
        }

        public static async Task<Harness> StartAsync(ILogger? logger = null)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            var accept = listener.AcceptSocketAsync();
            await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
            var accepted = await accept.WaitAsync(Generous);
            return new Harness(listener, client, accepted, logger ?? NullLogger.Instance);
        }

        public async Task SendAsync(byte[] frame) => await _client.SendAsync(frame, SocketFlags.None);

        public async Task<(ushort RmiId, byte[] Body)> NextPacketAsync()
            => await _packets.Reader.ReadAsync().AsTask().WaitAsync(Generous);

        public async Task<(ProudCoreOpCode Opcode, byte[] Payload)> NextCoreAsync()
            => await _core.Reader.ReadAsync().AsTask().WaitAsync(Generous);

        public async ValueTask DisposeAsync()
        {
            await _session.DisposeAsync();
            _client.Dispose();
            _listener.Stop();
        }
    }

    /// <summary>One S2C [13 57][CompactLen n][0x01][u16 rmiId][body] off a client socket.</summary>
    private static async Task<(ushort RmiId, byte[] Body)> ReadInternalRmiAsync(Socket client)
    {
        var head = await ReadExactAsync(client, 3);
        Assert.Equal(new byte[] { 0x13, 0x57 }, head[..2]);
        var lenBytes = await ReadExactAsync(client, head[2]);
        int len = head[2] switch
        {
            1 => lenBytes[0],
            2 => BinaryPrimitives.ReadUInt16LittleEndian(lenBytes),
            _ => BinaryPrimitives.ReadInt32LittleEndian(lenBytes),
        };
        var data = await ReadExactAsync(client, len);
        Assert.Equal(0x01, data[0]);
        return (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(1)), data[3..]);

        static async Task<byte[]> ReadExactAsync(Socket s, int n)
        {
            var buf = new byte[n];
            for (int got = 0; got < n;)
            {
                int r = await s.ReceiveAsync(buf.AsMemory(got), SocketFlags.None).AsTask().WaitAsync(Generous);
                if (r == 0) throw new EndOfStreamException();
                got += r;
            }
            return buf;
        }
    }

    private sealed class NoPersistence : RO2.Server.Zone.Services.IDirtySink
    {
        public void Mark(RO2.Server.Zone.Models.PlayerState player) { }
        public void Forget(uint characterId) { }
    }

    /// <summary>Formats every message at every level, as a Debug-enabled provider would.</summary>
    private sealed class CapturingLogger : ILogger
    {
        public readonly ConcurrentQueue<(LogLevel Level, string Text)> Lines = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Lines.Enqueue((logLevel, formatter(state, exception)));
    }
}
