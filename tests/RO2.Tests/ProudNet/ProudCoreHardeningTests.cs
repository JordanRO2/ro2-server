using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Network.Handlers;
using RO2.Network.Handlers.Shared;
using RO2.Network.Packets;
using RO2.Network.ProudNet;
using Xunit;

namespace RO2.Tests.ProudNet;

/// <summary>
/// ProudNet core hardening, state and control flow only (backlog S-H5, S-H6, S-H11, S-H12, S-H13): the
/// handshake order and the shared RSA key, the parser bounds, the dispatch gate and the AES integrity check,
/// bounded logging, and the accept loop. No wire layout changes; every frame below is built with the same
/// layouts the other ProudNet tests use (<see cref="ProudCoreMessageTypeTests"/>).
/// </summary>
public sealed class ProudCoreHardeningTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);
    private static readonly byte[] Rc4Key = Enumerable.Range(0, 64).Select(i => (byte)(i * 53 + 3)).ToArray();
    private static readonly byte[] AesKey = Enumerable.Range(0, 16).Select(i => (byte)(i * 17 + 5)).ToArray();

    // ---- S-H6: handshake state machine, shared RSA key ------------------------------------------

    [Fact]
    public void RsaKey_IsGeneratedOnceForTheProcess_AndSurvivesASessionDisposing()
    {
        var a = new ProudNetCrypto();
        var b = new ProudNetCrypto();
        a.GenerateKeyPair();
        b.GenerateKeyPair();

        // Same byte[] instance: one generation served both, not two keygens.
        Assert.Same(a.PublicKeyDer, b.PublicKeyDer);

        var before = a.PublicKeyDer;
        a.GenerateKeyPair(); // a second 0x2F on the same session re-generates nothing
        Assert.Same(before, a.PublicKeyDer);

        // Disposing one session must not dispose the key the others still decrypt with.
        a.Dispose();
        Assert.True(b.DecryptSessionKey(EncryptSessionKey(b.PublicKeyDer, new byte[32])));
        Assert.True(b.IsReady);
        b.Dispose();
    }

    [Fact]
    public void SessionKey_CannotBeReplacedOnceInstalled()
    {
        using var crypto = new ProudNetCrypto();
        crypto.GenerateKeyPair();
        Assert.True(crypto.DecryptSessionKey(EncryptSessionKey(crypto.PublicKeyDer, Enumerable.Repeat((byte)1, 32).ToArray())));

        // A second, perfectly valid key is refused: no re-key on a live session.
        Assert.False(crypto.DecryptSessionKey(EncryptSessionKey(crypto.PublicKeyDer, Enumerable.Repeat((byte)2, 32).ToArray())));
    }

    [Fact]
    public async Task SecondConnectionInit_IsIgnored_AndAnswersNothing()
    {
        await using var rig = await Loopback.StartAsync();
        var init = new InitProbe(NullLogger<InitProbe>.Instance);

        await init.HandleAsync(rig.Session, new byte[] { 0x00, 0x00, 0x00, 0x00 });
        Assert.Equal(HandshakeState.WaitingForSessionKey, rig.Session.Handshake);
        var key = rig.Session.Crypto.PublicKeyDer;
        await rig.ReadPolicyAsync();
        var peerAccept = await rig.ReadFrameAsync();
        Assert.Equal(0x04, peerAccept[0]);

        await init.HandleAsync(rig.Session, new byte[] { 0x00, 0x00, 0x00, 0x00 }); // the duplicate
        Assert.Equal(HandshakeState.WaitingForSessionKey, rig.Session.Handshake);
        Assert.Same(key, rig.Session.Crypto.PublicKeyDer);

        // TCP is ordered: if the duplicate had sent a policy or a PeerAccept, it would arrive before this.
        await rig.Session.SendInternalRmiAsync(1108, new byte[] { 0xAA });
        Assert.Equal(Convert.FromHexString("015404AA"), await rig.ReadFrameAsync());
    }

    [Fact]
    public async Task SessionKey_BeforeConnectionInit_DisconnectsTheSession()
    {
        await using var rig = await Loopback.StartAsync();
        var keyHandler = new KeyProbe(NullLogger<KeyProbe>.Instance);

        await keyHandler.HandleAsync(rig.Session, new byte[] { 0x00, 0x80, 0x00 }.Concat(new byte[128]).ToArray());

        Assert.Equal(HandshakeState.Failed, rig.Session.Handshake);
        Assert.True(await rig.IsClosedByServerAsync());
    }

    [Fact]
    public async Task UndecryptableSessionKey_IsDenied_NotAcknowledged()
    {
        await using var rig = await Loopback.StartAsync();
        var init = new InitProbe(NullLogger<InitProbe>.Instance);
        var keyHandler = new KeyProbe(NullLogger<KeyProbe>.Instance);
        await init.HandleAsync(rig.Session, new byte[4]);
        await rig.ReadPolicyAsync();
        await rig.ReadFrameAsync(); // PeerAccept

        await keyHandler.HandleAsync(rig.Session, new byte[] { 0x00, 0x80, 0x00 }.Concat(new byte[128]).ToArray());

        Assert.Equal(HandshakeState.Failed, rig.Session.Handshake);
        Assert.False(rig.Session.Crypto.IsReady);
        // No 0x06 ack: the next thing on the wire is the close.
        Assert.True(await rig.IsClosedByServerAsync());
    }

    [Fact]
    public async Task ValidSessionKey_IsAcknowledgedOnce_AndASecondOneDisconnects()
    {
        await using var rig = await Loopback.StartAsync();
        var init = new InitProbe(NullLogger<InitProbe>.Instance);
        var keyHandler = new KeyProbe(NullLogger<KeyProbe>.Instance);
        await init.HandleAsync(rig.Session, new byte[4]);
        await rig.ReadPolicyAsync();
        await rig.ReadFrameAsync();

        var key = KeyMessage(EncryptSessionKey(rig.Session.Crypto.PublicKeyDer, new byte[32]));
        await keyHandler.HandleAsync(rig.Session, key);
        Assert.Equal(new byte[] { 0x06 }, await rig.ReadFrameAsync());
        Assert.Equal(HandshakeState.WaitingForConnectionRequest, rig.Session.Handshake);
        Assert.True(rig.Session.Crypto.IsReady);

        await keyHandler.HandleAsync(rig.Session, key); // re-key on a live session
        Assert.Equal(HandshakeState.Failed, rig.Session.Handshake);
        Assert.True(await rig.IsClosedByServerAsync());
    }

    // ---- S-H12: parser robustness ---------------------------------------------------------------

    [Fact]
    public void PacketReader_SkipNegative_ThrowsAndDoesNotMoveBackwards()
    {
        var (threw, position) = SkipAfterOneByte(new byte[8], -1);
        Assert.True(threw);
        Assert.Equal(1, position);
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(int.MaxValue - 1)]
    [InlineData(int.MinValue)]
    [InlineData(8)]
    public void PacketReader_HostileCounts_ThrowInsteadOfWrappingPastTheBound(int count)
    {
        // Position 1 + int.MaxValue wraps to int.MinValue: the old sum compared it below the length and passed.
        var (threw, position) = SkipAfterOneByte(new byte[8], count);
        Assert.True(threw);
        Assert.Equal(1, position);
        Assert.True(ReadBytesThrows(new byte[8], count));
    }

    [Fact]
    public void PacketReader_ExactRemainder_StillReads()
    {
        var (threw, position) = SkipAfterOneByte(new byte[8], 7);
        Assert.False(threw);
        Assert.Equal(8, position);
    }

    [Fact]
    public async Task EncryptedWrapper_WithAFourByteWidth_ReadsTheWholeLength()
    {
        await using var rig = await Loopback.StartAsync(connected: true);
        rig.Session.Crypto.SetRc4Key(Rc4Key);

        // 300-byte plaintext [counter][01][rmiId][body 295]: the width byte is 4, so the length is four
        // bytes (300), not the low byte of it (44) that the old reader took.
        var plaintext = Rmi(counter: 7, rmiId: 1371, body: new byte[295]);
        Assert.Equal(300, plaintext.Length);
        await rig.SendAsync(Wrapper(0x25, mode: 0x02, Rc4(plaintext), width: 4));

        var (id, body) = await rig.NextPacketAsync();
        Assert.Equal(1371, id);
        Assert.Equal(295, body.Length);
    }

    [Theory]
    [InlineData(3)] // not a CompactLen width
    [InlineData(0)]
    [InlineData(16)]
    public async Task EncryptedWrapper_WithAnInvalidWidthByte_IsDropped(int width)
    {
        await using var rig = await Loopback.StartAsync(connected: true);
        rig.Session.Crypto.SetRc4Key(Rc4Key);
        var cipher = Rc4(Rmi(1, 1371, new byte[4]));

        await rig.SendAsync(ProudFrameCodec.EncodeFrame([0x25, 0x02, (byte)width, (byte)cipher.Length, .. cipher]));
        await rig.SendAsync(Wrapper(0x25, 0x02, Rc4(Rmi(2, 1372, new byte[4]))));

        var (id, _) = await rig.NextPacketAsync();
        Assert.Equal(1372, id); // the malformed one produced nothing; the marker is the first dispatch
    }

    [Fact]
    public async Task EncryptedWrapper_DeclaringMoreThanItHolds_IsDropped()
    {
        await using var rig = await Loopback.StartAsync(connected: true);
        rig.Session.Crypto.SetRc4Key(Rc4Key);
        var cipher = Rc4(Rmi(1, 1371, new byte[4]));

        // Declares 200 cipher bytes, carries 9.
        await rig.SendAsync(ProudFrameCodec.EncodeFrame([0x25, 0x02, 0x02, 200, 0, .. cipher]));
        await rig.SendAsync(Wrapper(0x25, 0x02, Rc4(Rmi(2, 1372, new byte[4]))));

        var (id, _) = await rig.NextPacketAsync();
        Assert.Equal(1372, id);
    }

    // ---- S-H5: dispatch gate, plaintext downgrade, AES integrity --------------------------------

    [Fact]
    public async Task Rmi_BeforeTheHandshakeCompletes_IsNotDispatched()
    {
        await using var rig = await Loopback.StartAsync(connected: false);
        rig.Session.Crypto.SetRc4Key(Rc4Key); // keyed, but the handshake never reached Connected
        var frame = Wrapper(0x25, 0x02, Rc4(Rmi(1, 1371, new byte[4])));

        await rig.SendAsync(frame);
        await rig.SendAsync(ProudFrameCodec.EncodeFrame([0x1B, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0])); // core marker
        await rig.NextCoreAsync();
        Assert.False(rig.PacketWaiting());
        Assert.Contains(rig.Log.Lines, l => l.Level == LogLevel.Warning && l.Text.Contains("before the ProudNet handshake completed"));

        // The same bytes once the handshake is done go through.
        rig.Session.Handshake = HandshakeState.Connected;
        await rig.SendAsync(frame);
        Assert.Equal(1371, (await rig.NextPacketAsync()).RmiId);
    }

    [Fact]
    public async Task PlaintextRmi_AfterTheHandshake_IsDropped()
    {
        await using var rig = await Loopback.StartAsync(connected: true);
        rig.Session.Crypto.SetRc4Key(Rc4Key);

        // [0x25][flags1 = 0][width 1][len][rmiId][body]: the plaintext form the old branch dispatched.
        await rig.SendAsync(ProudFrameCodec.EncodeFrame([0x25, 0x00, 0x01, 0x04, 0x5B, 0x05, 0x01, 0x02]));
        await rig.SendAsync(Wrapper(0x25, 0x02, Rc4(Rmi(2, 1372, new byte[4]))));

        Assert.Equal(1372, (await rig.NextPacketAsync()).RmiId);
        Assert.Contains(rig.Log.Lines, l => l.Level == LogLevel.Warning && l.Text.Contains("Dropping message 0x25 with encrypt mode 0x00"));
    }

    [Fact]
    public async Task RepeatedDrops_LogAtMostABoundedNumberOfLines()
    {
        await using var rig = await Loopback.StartAsync(connected: true);
        rig.Session.Crypto.SetRc4Key(Rc4Key);

        for (int i = 0; i < 60; i++)
            await rig.SendAsync(ProudFrameCodec.EncodeFrame([0x25, 0x00, 0x01, 0x04, 0x5B, 0x05, 0x01, 0x02]));
        await rig.SendAsync(Wrapper(0x25, 0x02, Rc4(Rmi(2, 1372, new byte[4]))));
        await rig.NextPacketAsync(); // every drop before it has been processed

        var lines = rig.Log.Lines.Count(l => l.Text.Contains("Dropping message 0x25 with encrypt mode 0x00"));
        Assert.InRange(lines, 1, 10);
    }

    [Fact]
    public void AesDecryptVerified_AcceptsAnIntactBlock_AndRejectsTamperedOnes()
    {
        using var sender = new ProudNetCrypto();
        using var receiver = new ProudNetCrypto();
        sender.SetSessionKey(AesKey);
        receiver.SetSessionKey(AesKey);

        var plain = Enumerable.Range(0, 40).Select(i => (byte)(i + 1)).ToArray();
        var cipher = sender.Encrypt(plain)!;
        Assert.Equal(48, cipher.Length);

        var ok = receiver.DecryptVerified(cipher, out var plainLength);
        Assert.NotNull(ok);
        Assert.Equal(40, plainLength);
        Assert.Equal(plain, ok[..40]);

        var flipped = (byte[])cipher.Clone();
        flipped[3] ^= 0x01;
        Assert.Null(receiver.DecryptVerified(flipped, out _));

        var swapped = (byte[])cipher.Clone(); // blocks 0 and 1 exchanged
        cipher.AsSpan(0, 16).CopyTo(swapped.AsSpan(16));
        cipher.AsSpan(16, 16).CopyTo(swapped.AsSpan(0));
        Assert.Null(receiver.DecryptVerified(swapped, out _));

        Assert.Null(receiver.DecryptVerified(cipher.AsSpan(0, 15), out _)); // not a whole block
    }

    [Fact]
    public void AesDecryptVerified_RejectsAnImpossiblePadLength()
    {
        using var receiver = new ProudNetCrypto();
        receiver.SetSessionKey(AesKey);

        var block = new byte[16];
        block[^1] = 0xFF; // pad length 255 in a 16-byte block
        using var aes = Aes.Create();
        aes.Key = AesKey;
        var cipher = aes.EncryptEcb(block, PaddingMode.None);

        Assert.Null(receiver.DecryptVerified(cipher, out _));
    }

    [Fact]
    public async Task AesRmi_WithABadCrc_IsNotDispatched_AndAnIntactOneIs()
    {
        await using var rig = await Loopback.StartAsync(connected: true);
        rig.Session.Crypto.SetSessionKey(AesKey);
        using var sender = new ProudNetCrypto();
        sender.SetSessionKey(AesKey);

        var cipher = sender.Encrypt(Rmi(1, 1371, new byte[40]))!;
        var tampered = (byte[])cipher.Clone();
        tampered[0] ^= 0x80;

        await rig.SendAsync(Wrapper(0x25, 0x01, tampered));
        await rig.SendAsync(Wrapper(0x25, 0x01, cipher));

        var (id, body) = await rig.NextPacketAsync();
        Assert.Equal(1371, id);
        Assert.True(body.Length >= 40); // the intact one, and only it
        Assert.False(rig.PacketWaiting());
    }

    // ---- S-H11: bounded logging ------------------------------------------------------------------

    [Fact]
    public async Task UnhandledRmi_WithA64KiBPayload_LogsABoundedLine()
    {
        var log = new Capture();
        var dispatcher = new RmiDispatcher(log);
        await using var rig = await Loopback.StartAsync();

        await dispatcher.DispatchAsync(rig.Session, 4242, new byte[64 * 1024]);

        var line = Assert.Single(log.Lines, l => l.Text.Contains("[UNHANDLED-RMI]"));
        Assert.True(line.Text.Length < 400, $"line is {line.Text.Length} chars");
        Assert.Contains("len=65536", line.Text);
        Assert.Contains("+65472 bytes", line.Text);
    }

    [Fact]
    public async Task UnhandledRmi_IdenticalWarnings_AreRateLimited_AndCountedAfterTheWindow()
    {
        var log = new Capture();
        var clock = new ManualTimeProvider();
        var dispatcher = new RmiDispatcher(log, clock);
        await using var rig = await Loopback.StartAsync();

        for (int i = 0; i < 100; i++)
            await dispatcher.DispatchAsync(rig.Session, 4242, new byte[4]);
        Assert.Equal(5, log.Lines.Count(l => l.Text.Contains("[UNHANDLED-RMI]")));

        await dispatcher.DispatchAsync(rig.Session, 4243, new byte[4]); // another opcode has its own budget
        Assert.Equal(6, log.Lines.Count(l => l.Text.Contains("[UNHANDLED-RMI]")));

        clock.Advance(TimeSpan.FromSeconds(11));
        await dispatcher.DispatchAsync(rig.Session, 4242, new byte[4]);
        Assert.Contains(log.Lines, l => l.Text.Contains("[UNHANDLED-RMI] 4242") && l.Text.Contains("suppressed=95"));
    }

    [Fact]
    public async Task MalformedRmiBody_LogsOneThrottledLineWithoutAStackTrace()
    {
        var log = new Capture();
        await using var provider = new ServiceCollection().BuildServiceProvider();
        var handler = new ReadsAnIntProbe(provider.GetRequiredService<IServiceScopeFactory>(), new Capture<ReadsAnIntProbe>(log));
        var dispatcher = new RmiDispatcher(log);
        dispatcher.Register(handler.Opcode, (s, p) => handler.HandleAsync(s, p), handler.Name);
        await using var rig = await Loopback.StartAsync();

        for (int i = 0; i < 100; i++)
            await dispatcher.DispatchAsync(rig.Session, handler.Opcode, ReadOnlyMemory<byte>.Empty);

        Assert.DoesNotContain(log.Lines, l => l.Exception != null); // no stack trace anywhere
        Assert.DoesNotContain(log.Lines, l => l.Level >= LogLevel.Error);
        var malformed = log.Lines.Count(l => l.Text.Contains("Malformed"));
        Assert.InRange(malformed, 1, 10); // 100 packets, at most 5 per sink (handler + dispatcher)
    }

    [Fact]
    public async Task NonMalformedHandlerFault_StillKeepsItsStackTrace()
    {
        var log = new Capture();
        await using var provider = new ServiceCollection().BuildServiceProvider();
        var handler = new ThrowsAServerFaultProbe(provider.GetRequiredService<IServiceScopeFactory>(), new Capture<ThrowsAServerFaultProbe>(log));
        await using var rig = await Loopback.StartAsync();

        await Assert.ThrowsAsync<NotSupportedException>(() => handler.HandleAsync(rig.Session, ReadOnlyMemory<byte>.Empty));

        Assert.Contains(log.Lines, l => l.Level == LogLevel.Error && l.Exception is NotSupportedException);
    }

    [Fact]
    public void CryptoFailure_GoesToTheLogger_NotTheConsole()
    {
        var log = new Capture();
        using var crypto = new ProudNetCrypto(log);
        crypto.SetSessionKey(AesKey);

        Assert.Null(crypto.Decrypt(new byte[7])); // not a whole AES block

        Assert.Contains(log.Lines, l => l.Level == LogLevel.Debug && l.Text.Contains("Crypto.Decrypt failed"));
    }

    // ---- S-H13: accept loop ---------------------------------------------------------------------

    [Fact]
    public async Task SlowSessionConnectedSubscriber_DoesNotStallTheAcceptLoop()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invoked = 0;
        await using var server = new ProudServer("SlowSubscriber", NullLogger.Instance);
        server.SessionConnected += async _ =>
        {
            Interlocked.Increment(ref invoked);
            await gate.Task; // never completes until the end of the test
        };
        await server.StartAsync(new IPEndPoint(IPAddress.Loopback, 0));

        var clients = new List<Socket>();
        try
        {
            for (int i = 0; i < 3; i++)
            {
                var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                clients.Add(client);
                await client.ConnectAsync(server.LocalEndPoint!);
            }

            var deadline = DateTime.UtcNow + Generous;
            while (server.SessionCount < 3 || Volatile.Read(ref invoked) < 3)
            {
                Assert.True(DateTime.UtcNow < deadline, $"accept loop stalled: {server.SessionCount} sessions, {invoked} notified");
                await Task.Delay(10);
            }
        }
        finally
        {
            gate.TrySetResult();
            foreach (var c in clients)
                c.Dispose();
        }
    }

    [Fact]
    public async Task FailingSessionConnectedSubscriber_IsLogged_AndTheSessionStaysAccepted()
    {
        var log = new Capture();
        await using var server = new ProudServer("FailingSubscriber", log);
        server.SessionConnected += async _ =>
        {
            await Task.Yield();
            throw new InvalidOperationException("subscriber failed");
        };
        await server.StartAsync(new IPEndPoint(IPAddress.Loopback, 0));

        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(server.LocalEndPoint!);

        var deadline = DateTime.UtcNow + Generous;
        while (!log.Lines.Any(l => l.Exception?.Message == "subscriber failed"))
        {
            Assert.True(DateTime.UtcNow < deadline, "the subscriber's failure was never logged");
            await Task.Delay(10);
        }

        Assert.Equal(1, server.SessionCount);
    }

    // ---- helpers --------------------------------------------------------------------------------

    private static byte[] EncryptSessionKey(byte[] publicKeyDer, byte[] sessionKey)
    {
        using var rsa = RSA.Create();
        rsa.ImportRSAPublicKey(publicKeyDer, out _);
        return rsa.Encrypt(sessionKey, RSAEncryptionPadding.OaepSHA1);
    }

    /// <summary>The 0x05 body: [keyType][u16 keyLen][RSA-encrypted key].</summary>
    private static byte[] KeyMessage(byte[] encryptedKey)
    {
        var m = new byte[3 + encryptedKey.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(m.AsSpan(1), (ushort)encryptedKey.Length);
        encryptedKey.CopyTo(m.AsSpan(3));
        return m;
    }

    private static (bool Threw, int Position) SkipAfterOneByte(byte[] buffer, int count)
    {
        var reader = new PacketReader(buffer);
        reader.ReadByte();
        try
        {
            reader.Skip(count);
            return (false, reader.Position);
        }
        catch (InvalidOperationException)
        {
            return (true, reader.Position);
        }
    }

    private static bool ReadBytesThrows(byte[] buffer, int count)
    {
        var reader = new PacketReader(buffer);
        reader.ReadByte();
        try
        {
            reader.ReadBytes(count);
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    /// <summary>Decrypted 0x25 message: [u16 counter][0x01][u16 rmiId][body].</summary>
    private static byte[] Rmi(ushort counter, ushort rmiId, byte[] body)
    {
        var m = new byte[2 + 1 + 2 + body.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(m, counter);
        m[2] = 0x01;
        BinaryPrimitives.WriteUInt16LittleEndian(m.AsSpan(3), rmiId);
        body.CopyTo(m.AsSpan(5));
        return m;
    }

    /// <summary>ProudNet's RC4 re-keys per message, so the transform is its own inverse.</summary>
    private static byte[] Rc4(byte[] plaintext)
    {
        using var crypto = new ProudNetCrypto();
        crypto.SetRc4Key(Rc4Key);
        return crypto.Rc4Decrypt(plaintext)!;
    }

    /// <summary>[type][mode][CompactLen n][cipher], framed. <paramref name="width"/> 0 picks 1 or 2 by size.</summary>
    private static byte[] Wrapper(byte type, byte mode, byte[] cipher, int width = 0)
    {
        if (width == 0)
            width = cipher.Length < 128 ? 1 : 2;
        var header = new byte[3 + width];
        header[0] = type;
        header[1] = mode;
        header[2] = (byte)width;
        for (int i = 0; i < width; i++)
            header[3 + i] = i < 4 ? (byte)(cipher.Length >> (8 * i)) : (byte)0;
        return ProudFrameCodec.EncodeFrame([.. header, .. cipher]);
    }

    private sealed class InitProbe(ILogger<InitProbe> logger) : ConnectionInitHandlerBase<InitProbe>(logger);

    private sealed class KeyProbe(ILogger<KeyProbe> logger) : EncryptedSessionKeyHandlerBase<KeyProbe>(logger);

    private sealed class ReadsAnIntProbe(IServiceScopeFactory scopes, ILogger<ReadsAnIntProbe> logger)
        : RmiHandlerBase<ReadsAnIntProbe>(scopes, logger)
    {
        public override ushort Opcode => 9001;
        public override string Name => "ReadsAnInt";

        protected override Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, IServiceProvider services, CancellationToken ct)
        {
            CreateReader(payload).ReadInt32(); // an empty body: PacketReader underflow
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowsAServerFaultProbe(IServiceScopeFactory scopes, ILogger<ThrowsAServerFaultProbe> logger)
        : RmiHandlerBase<ThrowsAServerFaultProbe>(scopes, logger)
    {
        public override ushort Opcode => 9002;
        public override string Name => "ThrowsAServerFault";

        protected override Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, IServiceProvider services, CancellationToken ct)
            => throw new NotSupportedException("server bug");
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class Capture : ILogger
    {
        public readonly record struct Line(LogLevel Level, string Text, Exception? Exception);

        private readonly List<Line> _lines = [];

        public IReadOnlyList<Line> Lines
        {
            get
            {
                lock (_lines)
                    return _lines.ToArray();
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_lines)
                _lines.Add(new Line(logLevel, formatter(state, exception), exception));
        }
    }

    private sealed class Capture<T>(Capture inner) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => inner.Log(logLevel, eventId, state, exception, formatter);
    }

    /// <summary>A real <see cref="ProudSession"/> on the accepting end of a loopback connection.</summary>
    private sealed class Loopback : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly Socket _client;
        private readonly Channel<(ushort, byte[])> _packets = Channel.CreateUnbounded<(ushort, byte[])>();
        private readonly Channel<(ProudCoreOpCode, byte[])> _core = Channel.CreateUnbounded<(ProudCoreOpCode, byte[])>();

        public ProudSession Session { get; }
        public Capture Log { get; }

        private Loopback(TcpListener listener, Socket client, Socket accepted, Capture log, bool connected)
        {
            _listener = listener;
            _client = client;
            Log = log;
            Session = new ProudSession(accepted, 77, log);
            if (connected)
                Session.Handshake = HandshakeState.Connected;
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

        public static async Task<Loopback> StartAsync(bool connected = false)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            var accept = listener.AcceptSocketAsync();
            await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
            var accepted = await accept.WaitAsync(Generous);
            return new Loopback(listener, client, accepted, new Capture(), connected);
        }

        public async Task SendAsync(byte[] frame) => await _client.SendAsync(frame, SocketFlags.None);

        public async Task<(ushort RmiId, byte[] Body)> NextPacketAsync()
            => await _packets.Reader.ReadAsync().AsTask().WaitAsync(Generous);

        public bool PacketWaiting() => _packets.Reader.TryPeek(out _);

        public async Task<(ProudCoreOpCode Type, byte[] Payload)> NextCoreAsync()
            => await _core.Reader.ReadAsync().AsTask().WaitAsync(Generous);

        /// <summary>The raw Flash policy the 0x2F handler sends first: ASCII up to and including its NUL.</summary>
        public async Task ReadPolicyAsync()
        {
            var one = new byte[1];
            do
            {
                await ReadExactAsync(one);
            }
            while (one[0] != 0);
        }

        /// <summary>One S2C frame's data: [13 57][u8 width][width bytes length][data].</summary>
        public async Task<byte[]> ReadFrameAsync()
        {
            var head = new byte[3];
            await ReadExactAsync(head);
            var lengthBytes = new byte[head[2]];
            await ReadExactAsync(lengthBytes);
            int length = head[2] switch
            {
                1 => lengthBytes[0],
                2 => BinaryPrimitives.ReadUInt16LittleEndian(lengthBytes),
                _ => BinaryPrimitives.ReadInt32LittleEndian(lengthBytes),
            };
            var data = new byte[length];
            await ReadExactAsync(data);
            return data;
        }

        /// <summary>True once the server closed this connection and nothing else was left to read.</summary>
        public async Task<bool> IsClosedByServerAsync()
        {
            try
            {
                return await _client.ReceiveAsync(new byte[16], SocketFlags.None).WaitAsync(Generous) == 0;
            }
            catch (SocketException)
            {
                return true;
            }
        }

        private async Task ReadExactAsync(byte[] buffer)
        {
            int read = 0;
            while (read < buffer.Length)
            {
                int n = await _client.ReceiveAsync(buffer.AsMemory(read), SocketFlags.None).AsTask().WaitAsync(Generous);
                if (n == 0)
                    throw new EndOfStreamException("connection closed mid-frame");
                read += n;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Session.DisposeAsync();
            _client.Dispose();
            _listener.Stop();
        }
    }
}
