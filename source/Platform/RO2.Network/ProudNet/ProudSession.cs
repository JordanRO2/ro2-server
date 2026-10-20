using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using RO2.Domain.Enums;
using RO2.Network.Packets;

namespace RO2.Network.ProudNet;

/// <summary>
/// ProudNet handshake state
/// </summary>
public enum HandshakeState
{
    Initial,
    WaitingForSessionKey,
    WaitingForConnectionRequest,
    WaitingForConnectAck,  // After 0x31, waiting for client's 0x32
    Connected,
    Failed
}

/// <summary>
/// Represents a client connection session using ProudNet protocol
/// </summary>
public partial class ProudSession : IAsyncDisposable
{
    // ProudNet message types that WRAP another message. Types 0x25 and 0x26 go to the encrypted-message
    // handler and 0x27 to the decompressor; what comes out is re-dispatched by ITS first byte. There is
    // no 0x41 or 0x42 wrapper type.
    //
    //   0x25 = [0x25][u8 encMode][CompactLen n][n bytes cipher] -> [u16 encryptCount][message]
    //   0x26 = [0x26][u8 encMode][CompactLen n][n bytes cipher] -> [message]           (NO counter)
    //   0x27 = [0x27][CompactLen deflatedLen][CompactLen inflatedLen][zlib stream]     -> [message]
    //
    // The counter is the one difference between 0x25 and 0x26: the receiver reads and checks it (against
    // the expected decrypt count) only when the type is 37, and the sender writes it only for a reliable
    // send, which is tagged 37; every other send is tagged 38 and has no counter.
    // (0x26 is not a ping: UnreliablePing is 0x1B, a plain core message. The whole type table is
    // ProudCoreOpCode.)
    private const byte MessageTypeRmi = (byte)ProudCoreOpCode.Rmi;
    private const byte MessageTypeEncryptedReliable = (byte)ProudCoreOpCode.EncryptedReliable;
    private const byte MessageTypeEncryptedNoCounter = (byte)ProudCoreOpCode.EncryptedUnreliable;
    private const byte MessageTypeCompressed = (byte)ProudCoreOpCode.Compressed;

    // Encrypt-mode byte of a 0x25 / 0x26 wrapper (see EncryptMode): 1 = AES, 2 = RC4; 0 is this server's
    // own unencrypted form.
    private const byte EncryptModeNone = (byte)EncryptMode.None;
    private const byte EncryptModeAes = (byte)EncryptMode.Secure;
    private const byte EncryptModeRc4 = (byte)EncryptMode.Fast;

    // CompactLen width byte (see TryReadCompactLength) of the lengths this session writes.
    private const byte CompactLenWidth1 = 1;
    private const byte CompactLenWidth2 = 2;

    /// <summary>Largest payload whose length still fits the one-byte (signed) form of a CompactLen.</summary>
    private const int MaxOneByteLength = sbyte.MaxValue;

    /// <summary>A 0x25 / 0x26 wrapper header before its length: [type][encMode][CompactLen width byte].</summary>
    private const int WrapperHeaderSize = 3;

    /// <summary>Size of the u16 encrypt counter that precedes the message inside a 0x25 plaintext.</summary>
    private const int EncryptCounterSize = 2;

    /// <summary>Offset of the u16 little-endian RMI id inside an RMI message: [0x01][u16 rmiId][body].</summary>
    private const int RmiIdOffset = 1;

    /// <summary>Size of an RMI message header: the type byte plus the u16 RMI id. The body starts here.</summary>
    private const int RmiHeaderSize = 3;

    /// <summary>Size of the frame magic ([13 57], <see cref="ProudConstants.NetMagic"/>) that opens every frame.</summary>
    private const int FrameMagicSize = 2;

    /// <summary>Bytes requested from the pipe for each socket read.</summary>
    private const int ReceiveBufferHint = 4096;

    /// <summary>How long an orderly disconnect waits for the receive/process tasks to finish.</summary>
    private static readonly TimeSpan DisconnectWait = TimeSpan.FromSeconds(5);

    /// <summary>How long disposal waits for the receive/process tasks to finish.</summary>
    private static readonly TimeSpan DisposeWait = TimeSpan.FromSeconds(1);

    /// <summary>Warnings allowed per kind per throttle window before the rest are only counted.</summary>
    private const int WarningsPerWindow = 10;

    /// <summary>
    /// Largest inflated message this session accepts. A 0x27 whose inflatedLen exceeds this is
    /// rejected before inflating, so a forged inflatedLen cannot make us allocate without bound.
    /// Legitimate messages stay far below it (compressed whispers inflate to under 300 bytes).
    /// </summary>
    private const int MaxInflatedLength = 64 * 1024;

    private readonly Socket _socket;
    private readonly ILogger _logger;
    private readonly Pipe _receivePipe;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private bool _disposed;

    public uint SessionId { get; }
    public IPEndPoint? RemoteEndPoint { get; }
    public bool IsConnected => _socket.Connected && !_disposed;
    public DateTime ConnectedAt { get; }
    public DateTime LastActivity { get; private set; }

    /// <summary>
    /// When bytes last ARRIVED from the peer. Kept apart from <see cref="LastActivity"/> (which sends
    /// also move) because the idle sweep must not be fed by our own outbound traffic: a peer that
    /// sends nothing is dead or hostile no matter what we write to it.
    /// </summary>
    public DateTime LastReceived { get; private set; }

    private readonly TimeProvider _time;

    // Drops a peer can trigger at will are logged through this (S-H11): the first few per kind per window,
    // the rest counted. Per session, so one noisy peer cannot silence another's lines.
    private readonly WarningThrottle _warnThrottle;

    private const int WarnCompressedDropped = 1;
    private const int WarnNestedCompressed = 2;
    private const int WarnCompressedNotRmi = 3;
    private const int WarnEncryptedNotRmi = 4;
    private const int WarnUnknownEncryptMode = 5;
    private const int WarnDecryptFailed = 6;
    private const int WarnCryptoNotReady = 7;
    private const int WarnRmiBeforeHandshake = 8;
    private const int WarnMalformedHeader = 9;
    private const int WarnFrameFault = 10;

    private bool AllowWarning(int kind) => _warnThrottle.TryAllow(kind, out _);

    // ProudNet handshake state. Driven by the core handlers (0x2F -> 0x05 -> 0x07/0x31 -> Connected); a
    // step taken out of order calls AbortHandshake, which parks it at Failed.
    public HandshakeState Handshake { get; set; } = HandshakeState.Initial;

    // ProudNet encryption
    public ProudNetCrypto Crypto { get; }

    // Session state
    public SessionState State { get; set; } = SessionState.Connected;
    public uint? AccountId { get; set; }
    public uint? CharacterId { get; set; }
    public string? Username { get; set; }
    public bool ZoneGreetingSent { get; set; }

    /// <summary>
    /// Log packet hex with direction (&lt;&lt;&lt; S2C / &gt;&gt;&gt; C2S) and opcode identification
    /// </summary>
    /// <remarks>
    /// Runs on every packet sent and every socket read, so the level check comes FIRST: the hex
    /// string (the bulk of the cost: 551 -> 262 B per SendInternalRmiAsync with only this guard)
    /// is built only when Debug is on. Callers that build <paramref name="detail"/> with
    /// interpolation guard the call too, for the same reason.
    /// </remarks>
    private void LogPacket(string direction, ReadOnlySpan<byte> data, string? detail = null)
    {
        if (data.Length == 0 || !_logger.IsEnabled(LogLevel.Debug)) return;
        var hex = Convert.ToHexString(data);
        var opInfo = "";
        // Try to identify ProudNet opcode from frame payload
        // Frame: [13 57][scalar][payload...], payload[0] = opcode
        if (data.Length >= 4 && BinaryPrimitives.ReadUInt16LittleEndian(data) == ProudConstants.NetMagic)
        {
            // The scalar length is [u8 width][width bytes] (width 1, 2 or 4), so the payload starts after
            // the magic, the width byte and the length itself.
            int payloadStart = data[FrameMagicSize] switch
            {
                1 or 2 or 4 => FrameMagicSize + 1 + data[FrameMagicSize],
                _ => 0
            };
            if (payloadStart > 0 && payloadStart < data.Length)
            {
                byte opcode = data[payloadStart];
                // Labels for the core messages seen on this server's wire. 0x31 and 0x32 are named by the
                // legacy handlers' aliases: the session-key success and the connect-success the handlers
                // exchange under those values.
                opInfo = opcode switch
                {
                    (byte)ProudCoreOpCode.Rmi => " [InternalRmi]",
                    (byte)ProudCoreOpCode.NotifyServerConnectionHint => " [PeerAccept]",
                    (byte)ProudCoreOpCode.EncryptedSessionKey => " [EncryptedSessionKey]",
                    (byte)ProudCoreOpCode.SessionKeySuccess => " [SessionKeyAck]",
                    (byte)ProudCoreOpCode.ClientConnectInfo => " [ClientConnectInfo]",
                    (byte)ProudCoreOpCode.ConnectSuccess => " [ConnectResponse]",
                    (byte)ProudCoreOpCode.UnreliablePing => " [Heartbeat]",
                    (byte)ProudCoreOpCode.SpeedHackDetectorPing => " [Heartbeat]",
                    (byte)ProudCoreOpCode.UnreliablePong => " [Heartbeat]",
                    (byte)ProudCoreOpCode.EncryptedReliable => " [RMI]",
                    (byte)ProudCoreOpCode.RequestServerConnectionHint => " [ConnectionInit]",
                    (byte)ProudCoreOpCode.NotifyCSSessionKeySuccess => " [NotifySessionKey]",
                    (byte)ProudCoreOpCode.NotifyServerConnectSuccess => " [ServerConnectSuccess]",
                    _ => $" [0x{opcode:X2}]"
                };
            }
        }
        if (detail != null)
            Log.PacketWithDetail(_logger, direction, data.Length, opInfo, detail, hex);
        else
            Log.Packet(_logger, direction, data.Length, opInfo, hex);
    }

    /// <summary>
    /// Build a 0x25 RMI message with proper encrypt-mode byte and length encoding.
    /// The CompactLen width byte is 2 (2-byte length LE) for data &gt; 127 bytes; for data &lt;= 127 bytes it
    /// is 1 (1-byte length) when <paramref name="encryptMode"/> is AES and 0 otherwise (this server's own
    /// unencrypted form).
    /// </summary>
    private static byte[] BuildRmiMessage(byte encryptMode, ReadOnlySpan<byte> data)
    {
        if (data.Length > MaxOneByteLength)
        {
            // [0x25][encMode][0x02][len:2 LE][data]
            var msg = new byte[WrapperHeaderSize + sizeof(ushort) + data.Length];
            msg[0] = MessageTypeEncryptedReliable;
            msg[1] = encryptMode;
            msg[2] = CompactLenWidth2;
            BinaryPrimitives.WriteUInt16LittleEndian(msg.AsSpan(WrapperHeaderSize), (ushort)data.Length);
            data.CopyTo(msg.AsSpan(WrapperHeaderSize + sizeof(ushort)));
            return msg;
        }
        else
        {
            // [0x25][encMode][width byte][len:1][data]
            var msg = new byte[WrapperHeaderSize + sizeof(byte) + data.Length];
            msg[0] = MessageTypeEncryptedReliable;
            msg[1] = encryptMode;
            msg[2] = (encryptMode == EncryptModeAes) ? CompactLenWidth1 : (byte)0x00;
            msg[3] = (byte)data.Length;
            data.CopyTo(msg.AsSpan(WrapperHeaderSize + sizeof(byte)));
            return msg;
        }
    }

    public event Func<ProudSession, ushort, ReadOnlyMemory<byte>, Task>? PacketReceived;
    public event Func<ProudSession, ProudCoreOpCode, ReadOnlyMemory<byte>, Task>? CoreMessageReceived;
    public event Func<ProudSession, Task>? Disconnected;

    public ProudSession(Socket socket, uint sessionId, ILogger logger, TimeProvider? timeProvider = null)
    {
        _socket = socket;
        SessionId = sessionId;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
        _warnThrottle = new WarningThrottle(_time, perWindow: WarningsPerWindow);
        Crypto = new ProudNetCrypto(logger);
        // The writer pauses once this many bytes are buffered unparsed. It must sit above the largest
        // frame (header + MaxFrameBytes) or a legitimate maximum-size frame would never complete: the
        // writer would be paused one byte short of the body the reader is waiting for.
        _receivePipe = new Pipe(new PipeOptions(
            pauseWriterThreshold: 2 * ProudFrameCodec.MaxFrameBytes,
            resumeWriterThreshold: ProudFrameCodec.MaxFrameBytes,
            useSynchronizationContext: false
        ));

        RemoteEndPoint = socket.RemoteEndPoint as IPEndPoint;
        ConnectedAt = _time.GetUtcNow().UtcDateTime;
        LastActivity = ConnectedAt;
        LastReceived = ConnectedAt;
    }

    /// <summary>
    /// Why the idle sweep should close this session at <paramref name="now"/>, or null if it should
    /// live. A session that has not finished the ProudNet handshake (<see cref="HandshakeState.Connected"/>)
    /// gets the short <paramref name="handshakeTimeout"/> measured from accept; a matured one is closed
    /// after <paramref name="idleTimeout"/> without inbound bytes.
    /// </summary>
    public string? GetTimeoutReason(DateTime now, TimeSpan handshakeTimeout, TimeSpan idleTimeout)
    {
        if (Handshake != HandshakeState.Connected && now - ConnectedAt >= handshakeTimeout)
            return $"handshake not completed within {handshakeTimeout.TotalSeconds:0.###}s";
        if (now - LastReceived >= idleTimeout)
            return $"no data received for {idleTimeout.TotalSeconds:0.###}s";
        return null;
    }

    /// <summary>
    /// Start receiving data from the client
    /// </summary>
    public void StartReceiving()
    {
        _cts = new CancellationTokenSource();
        _receiveTask = Task.WhenAll(
            ReceiveAsync(_cts.Token),
            ProcessAsync(_cts.Token)
        );
    }

    private async Task ReceiveAsync(CancellationToken ct)
    {
        var writer = _receivePipe.Writer;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var memory = writer.GetMemory(ReceiveBufferHint);
                var bytesRead = await _socket.ReceiveAsync(memory, SocketFlags.None, ct);

                if (bytesRead == 0)
                {
                    _logger.LogDebug("Session {SessionId}: Connection closed by client", SessionId);
                    break;
                }

                // Log raw bytes. Straight from the pipe's buffer: it stays valid
                // until Advance below, and LogPacket is synchronous, so the copy this used to make
                // (which existed only to be logged) is not needed.
                LogPacket(">>>", memory.Span[..bytesRead]);

                LastReceived = LastActivity = _time.GetUtcNow().UtcDateTime;
                writer.Advance(bytesRead);

                var result = await writer.FlushAsync(ct);
                if (result.IsCompleted)
                    break;
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException ex)
        {
            _logger.LogDebug(ex, "Session {SessionId}: Socket error", SessionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Session {SessionId}: Receive error", SessionId);
        }
        finally
        {
            await writer.CompleteAsync();
        }
    }

    private async Task ProcessAsync(CancellationToken ct)
    {
        var reader = _receivePipe.Reader;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await reader.ReadAsync(ct);
                var buffer = result.Buffer;

                // Stops at once when a handler aborted the session (AbortHandshake cancels ct): the frames
                // still buffered behind a handshake violation are not processed.
                while (!ct.IsCancellationRequested && TryParseProudFrame(ref buffer, out var frameData))
                {
                    try
                    {
                        await ProcessFrameAsync(frameData);
                    }
                    catch (Exception ex)
                    {
                        // A malformed body is the peer's doing and repeats at its pace: one throttled
                        // line, no stack trace. Anything else is a server fault and keeps its trace.
                        if (!WarningThrottle.IsMalformedPacket(ex))
                            _logger.LogError(ex, "Session {SessionId}: Error processing frame", SessionId);
                        else if (AllowWarning(WarnFrameFault))
                            _logger.LogWarning("Session {SessionId}: Malformed frame dropped: {Reason}", SessionId, ex.Message);
                    }
                }

                reader.AdvanceTo(buffer.Start, buffer.End);

                if (result.IsCompleted)
                    break;
            }
        }
        catch (OperationCanceledException) { }
        catch (InvalidDataException ex)
        {
            _logger.LogWarning(ex, "Session {SessionId}: Invalid data received, disconnecting", SessionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Session {SessionId}: Process error", SessionId);
        }
        finally
        {
            await reader.CompleteAsync();
            await OnDisconnectedAsync();
        }
    }

    /// <summary>
    /// Process a ProudNet frame
    /// </summary>
    /// <param name="frameData">The message: its first byte is the ProudNet message type.</param>
    /// <param name="inflated">
    /// True when this message came out of a 0x27 wrapper. A second 0x27 inside it is refused: the
    /// client never nests (Send_CompressLayer compresses once, then hands the result to
    /// Send_SecureLayer), and refusing keeps a small frame from expanding more than once.
    /// </param>
    private async Task ProcessFrameAsync(ReadOnlyMemory<byte> frameData, bool inflated = false)
    {
        if (frameData.Length == 0 || Handshake == HandshakeState.Failed)
            return;

        var span = frameData.Span;
        var opcode = span[0];
        var payload = frameData.Slice(1);

        Log.CoreMessage(_logger, SessionId, opcode, payload.Length);

        // The two encrypted wrappers (reliable 0x25 with its counter, unreliable 0x26 without one)
        if (opcode == MessageTypeEncryptedReliable || opcode == MessageTypeEncryptedNoCounter)
        {
            await ProcessEncryptedAsync(opcode, payload, inflated);
        }
        else if (opcode == MessageTypeCompressed)
        {
            // Compressed but not encrypted: Send_SecureLayer skips encryption when the RMI context
            // has no encrypt mode, so the 0x27 message goes out bare. What it inflates to is routed
            // exactly as that message would have been had it arrived uncompressed.
            if (inflated)
            {
                if (AllowWarning(WarnNestedCompressed))
                    Log.NestedCompressedDropped(_logger, SessionId);
                return;
            }

            var message = Inflate(payload.Span);
            if (message != null)
                await ProcessFrameAsync(message, inflated: true);
        }
        else
        {
            // Every other type is a core message for the core handlers, keyed by ProudCoreOpCode
            // (0x2F handshake, 0x1B ping, ...). A type no handler takes, such as 0x41 / 0x42, is logged
            // by the server's wiring as unhandled and gets no answer.
            var coreOpcode = (ProudCoreOpCode)opcode;
            Log.InternalMessage(_logger, SessionId, coreOpcode);

            if (CoreMessageReceived != null)
            {
                await CoreMessageReceived.Invoke(this, coreOpcode, payload);
            }
        }
    }

    /// <summary>
    /// Handle an encrypted message: 0x25 (with the u16 encrypt counter) or 0x26 (without one).
    /// Both share the header [encMode][CompactLen][cipher] (the mode byte, then the length). 0x25 additionally keeps this server's own unencrypted form
    /// (encMode 0, [rmiId][payload] in the clear) that SendGameRmiAsync writes.
    /// </summary>
    private async Task ProcessEncryptedAsync(byte messageType, ReadOnlyMemory<byte> payload, bool inflated)
    {
        // Wrapper body: [flags1 = encMode][CompactLen n: u8 width + width bytes LE][n bytes cipher]. The
        // shortest body holds the mode, a width byte and one length byte.
        if (payload.Length < 3)
            return;

        var flags1 = payload.Span[0];
        var flags2 = payload.Span[1]; // the CompactLen width byte; kept only for the debug line below

        // Send_SecureLayer emits no encrypt mode but AES (1) and RC4 (2), and the handshake has negotiated
        // encryption by the time any RMI flows. A 0 (plaintext RMI), a 0 length or any other mode is
        // dropped before its body is parsed or dispatched; accepting it would be a downgrade (S-H5).
        if (flags1 != EncryptModeAes && flags1 != EncryptModeRc4)
        {
            if (AllowWarning(WarnUnknownEncryptMode))
                Log.UnknownEncryptModeDropped(_logger, SessionId, messageType, flags1, payload.Length);
            return;
        }

        // The length is a CompactLen (width 1/2/4/8, signed, never negative), read by the same
        // reader Inflate uses.
        int headerLen = sizeof(byte); // past the encMode byte
        if (!TryReadCompactLength(payload.Span, ref headerLen, out int dataLen))
        {
            if (AllowWarning(WarnMalformedHeader))
                Log.EncryptedHeaderDropped(_logger, SessionId, messageType, flags1, flags2);
            return;
        }

        var data = payload.Slice(headerLen);

        Log.RmiFlags(_logger, SessionId, flags1, flags2, dataLen, data.Length);

        // The declared length must be present: a header promising more than the frame holds is not parsed.
        if (dataLen <= 0 || dataLen > data.Length)
        {
            if (AllowWarning(WarnMalformedHeader))
                Log.EncryptedLengthDropped(_logger, SessionId, messageType, dataLen, data.Length);
            return;
        }

        // Only 0x25 has the counter in front of the decrypted message; 0x26 starts at the type byte.
        int counterBytes = messageType == MessageTypeEncryptedReliable ? EncryptCounterSize : 0;
        var encrypted = data[..dataLen];

        if (flags1 == EncryptModeAes)
        {
            // AES-encrypted RMI
            if (!Crypto.IsReady)
            {
                if (AllowWarning(WarnCryptoNotReady))
                    _logger.LogWarning("Session {SessionId}: Crypto not ready (IsReady={Ready}), dropping AES message",
                        SessionId, Crypto.IsReady);
                return;
            }

            // Decrypt reads the span directly; the array copy this used to make served only
            // the hex log line, which is now built only when Debug is on.
            if (_logger.IsEnabled(LogLevel.Debug))
                Log.EncryptedData(_logger, SessionId, encrypted.Length, Convert.ToHexString(encrypted.Span));

            // The block's CRC32 and pad length are checked: a flipped or swapped ciphertext block fails here.
            var decrypted = Crypto.DecryptVerified(encrypted.Span, out int plainLength);
            // [counter][type][u16 rmiId] is the shortest dispatchable 0x25 (5 bytes, as before).
            if (decrypted != null && plainLength >= counterBytes + RmiHeaderSize)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                    Log.DecryptedData(_logger, SessionId, decrypted.Length, Convert.ToHexString(decrypted));

                await DispatchDecryptedAsync(decrypted, counterBytes, rc4: false, inflated);
            }
            else if (AllowWarning(WarnDecryptFailed))
            {
                _logger.LogWarning("Session {SessionId}: Dropping AES RMI (decrypt, CRC32/pad check or length failed)", SessionId);
            }
        }
        else
        {
            // RC4 fast-mode encrypted RMI
            if (!Crypto.IsRc4Ready)
            {
                if (AllowWarning(WarnCryptoNotReady))
                    _logger.LogWarning("Session {SessionId}: RC4 not ready (IsRc4Ready={Ready}), dropping flags1=0x02 message",
                        SessionId, Crypto.IsRc4Ready);
                return;
            }

            if (_logger.IsEnabled(LogLevel.Debug))
                Log.Rc4EncryptedData(_logger, SessionId, encrypted.Length, Convert.ToHexString(encrypted.Span));

            var decrypted = Crypto.Rc4Decrypt(encrypted.Span);
            if (decrypted != null && decrypted.Length >= counterBytes + RmiHeaderSize)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                    Log.Rc4DecryptedData(_logger, SessionId, decrypted.Length, Convert.ToHexString(decrypted));

                await DispatchDecryptedAsync(decrypted, counterBytes, rc4: true, inflated);
            }
            else if (AllowWarning(WarnDecryptFailed))
            {
                _logger.LogWarning("Session {SessionId}: Failed to RC4 decrypt RMI (result null or too short)", SessionId);
            }
        }
    }

    /// <summary>
    /// The one place a decoded RMI leaves the transport for the game handlers. Nothing is dispatched until
    /// the ProudNet handshake has completed: a peer that has not finished it has no session key and no
    /// business running handlers (S-H5). Dropped with a throttled warning otherwise.
    /// </summary>
    private async Task RaiseRmiAsync(ushort rmiId, ReadOnlyMemory<byte> payload)
    {
        if (Handshake != HandshakeState.Connected)
        {
            if (AllowWarning(WarnRmiBeforeHandshake))
                Log.RmiBeforeHandshakeDropped(_logger, SessionId, rmiId, Handshake);
            return;
        }

        if (PacketReceived != null)
        {
            await PacketReceived.Invoke(this, rmiId, payload);
        }
    }

    /// <summary>
    /// Hard-disconnect a session that broke the handshake order (a 0x05 before 0x2F, a second key, a key
    /// that does not decrypt). Marks it <see cref="HandshakeState.Failed"/> and closes the socket without
    /// waiting: unlike <see cref="DisconnectAsync"/> it is safe to call from inside a handler, which runs on
    /// the very task DisconnectAsync would wait for.
    /// </summary>
    public void AbortHandshake(string reason)
    {
        if (_disposed || Handshake == HandshakeState.Failed)
            return;

        Handshake = HandshakeState.Failed;
        Log.HandshakeAborted(_logger, SessionId, reason);

        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException) { }

        try
        {
            _socket.Shutdown(SocketShutdown.Both);
        }
        catch { }
    }

    /// <summary>
    /// Dispatch a decrypted message by its type byte, the way the ProudNet layer re-enters
    /// itself on it. The RMI id is read only for a type 0x01 message; a compressed message is
    /// inflated first and then dispatched by its own type byte.
    /// </summary>
    private async Task DispatchDecryptedAsync(byte[] decrypted, int counterBytes, bool rc4, bool inflated)
    {
        var message = decrypted.AsMemory(counterBytes);
        var messageType = message.Span[0];

        if (messageType == MessageTypeRmi)
        {
            // Decrypted format: [Prefix:3][RmiId:2][Payload:...] for 0x25 (the prefix is the u16
            // encrypt counter + the 0x01 type byte); [0x01][RmiId:2][Payload:...] for 0x26.
            var rmiId = BinaryPrimitives.ReadUInt16LittleEndian(message.Span[RmiIdOffset..]);
            var rmiPayload = message.Slice(RmiHeaderSize);

            if (rc4)
                Log.Rc4DecryptedRmi(_logger, SessionId, rmiId, rmiId, rmiPayload.Length);
            else
                Log.DecryptedRmi(_logger, SessionId, rmiId, rmiId, rmiPayload.Length);

            await RaiseRmiAsync(rmiId, rmiPayload);
        }
        else if (messageType == MessageTypeCompressed)
        {
            // Compressed, then encrypted. The compress layer sits above the secure layer, so the
            // 0x27 is the whole message the cipher carried (after the counter, for a 0x25). A compressed
            // 1316 ReqPCWhisper arrives as a 0x26: its plaintext starts at 0x27 and it consumes no
            // encrypt-counter value.
            if (inflated)
            {
                if (AllowWarning(WarnNestedCompressed))
                    Log.NestedCompressedDropped(_logger, SessionId);
                return;
            }

            var inner = Inflate(message.Span[sizeof(byte)..]); // past the 0x27 type byte
            if (inner == null)
                return;

            if (inner.Length < RmiHeaderSize || inner[0] != MessageTypeRmi)
            {
                if (AllowWarning(WarnCompressedNotRmi))
                    Log.CompressedNotRmiDropped(_logger, SessionId, inner[0], inner.Length);
                return;
            }

            // Inflates to an ordinary [0x01][u16 rmiId][body], dispatched like any decrypted RMI,
            // so the handler (WhisperHandler for 1316) sees the same body an uncompressed one has.
            var rmiId = BinaryPrimitives.ReadUInt16LittleEndian(inner.AsSpan(RmiIdOffset));
            var rmiPayload = inner.AsMemory(RmiHeaderSize);

            Log.CompressedRmi(_logger, SessionId, rmiId, rmiId, rmiPayload.Length);

            await RaiseRmiAsync(rmiId, rmiPayload);
        }
        else if (AllowWarning(WarnEncryptedNotRmi))
        {
            Log.EncryptedNotRmiDropped(_logger, SessionId, messageType, message.Length);
        }
    }

    /// <summary>
    /// Inflate the body of a 0x27 message (everything after the type byte):
    /// [CompactLen deflatedLen][CompactLen inflatedLen][deflatedLen bytes of zlib].
    /// Refuse an inflatedLen above the max message length, then accept only an
    /// inflate that succeeds with exactly inflatedLen bytes out. The output buffer is exactly
    /// inflatedLen long, so a stream that would inflate past its declared size stops there
    /// (DestinationTooSmall) and is refused rather than grown.
    /// </summary>
    /// <returns>The inflated message (first byte = its type), or null after logging why not.</returns>
    private byte[]? Inflate(ReadOnlySpan<byte> body)
    {
        int pos = 0;
        if (!TryReadCompactLength(body, ref pos, out int deflatedLen) ||
            !TryReadCompactLength(body, ref pos, out int inflatedLen))
        {
            if (AllowWarning(WarnCompressedDropped))
                Log.CompressedDropped(_logger, SessionId, "malformed length header");
            return null;
        }

        if (inflatedLen <= 0 || inflatedLen > MaxInflatedLength)
        {
            if (AllowWarning(WarnCompressedDropped))
                Log.CompressedDropped(_logger, SessionId, $"inflated length {inflatedLen} outside 1..{MaxInflatedLength}");
            return null;
        }

        // The client passes deflatedLen straight to zlib without bounding it; we must.
        if (deflatedLen <= 0 || deflatedLen > body.Length - pos)
        {
            if (AllowWarning(WarnCompressedDropped))
                Log.CompressedDropped(_logger, SessionId, $"deflated length {deflatedLen} but {body.Length - pos} bytes follow");
            return null;
        }

        var message = new byte[inflatedLen];
        OperationStatus status;
        int written;
        // Streamless span API (.NET 11): no MemoryStream/ZLibStream pair per message. The zlib
        // wrapper (78 9C here) and its Adler-32 are checked; a bad checksum is InvalidData.
        using (var decoder = new ZLibDecoder())
            status = decoder.Decompress(body.Slice(pos, deflatedLen), message, out _, out written);

        if (status != OperationStatus.Done || written != inflatedLen)
        {
            if (AllowWarning(WarnCompressedDropped))
                Log.CompressedDropped(_logger, SessionId, $"zlib {status}, {written} of {inflatedLen} bytes");
            return null;
        }

        Log.Compressed(_logger, SessionId, deflatedLen, inflatedLen);
        return message;
    }

    /// <summary>
    /// ProudNet CompactLen:
    /// [u8 width][width bytes, little endian, signed], width 1, 2, 4 or 8. A negative length is
    /// refused.
    /// </summary>
    private static bool TryReadCompactLength(ReadOnlySpan<byte> source, ref int pos, out int value)
    {
        value = 0;
        if (pos >= source.Length)
            return false;

        int width = source[pos];
        if (width is not (1 or 2 or 4 or 8) || source.Length - pos - 1 < width)
            return false;

        var bytes = source.Slice(pos + 1, width);
        long length = width switch
        {
            1 => (sbyte)bytes[0],
            2 => BinaryPrimitives.ReadInt16LittleEndian(bytes),
            4 => BinaryPrimitives.ReadInt32LittleEndian(bytes),
            _ => BinaryPrimitives.ReadInt64LittleEndian(bytes),
        };

        if (length is < 0 or > int.MaxValue)
            return false;

        pos += 1 + width;
        value = (int)length;
        return true;
    }

    /// <summary>
    /// Parse ProudNet frame from buffer using ProudFrameCodec
    /// Frame format: [Magic:2][Length:scalar][Data:...]
    /// </summary>
    private bool TryParseProudFrame(ref ReadOnlySequence<byte> buffer, out ReadOnlyMemory<byte> frameData)
    {
        return ProudFrameCodec.TryParseFrame(ref buffer, out frameData);
    }

    /// <summary>
    /// Send an RMI packet to the client.
    /// TEST MODE (all S2C UNENC): forced through the internal/unencrypted transport (opcode 0x01),
    /// the same path as <see cref="SendInternalRmiAsync"/>. The client has no per-RMI encryption gate - the
    /// encrypted 0x25 path only decrypts and re-dispatches into the same handler - so every RMI is accepted
    /// unencrypted. The handshake/session-key path uses SendCoreMessageAsync / ProudNetCrypto (not
    /// SendRmiAsync), so it is unaffected. To restore per-RMI encryption later, revert this delegation
    /// (the original encrypted framing is preserved in git history).
    /// </summary>
    public Task SendRmiAsync(ushort rmiId, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
        => SendInternalRmiAsync(rmiId, payload, ct);

    /// <summary>
    /// Send an RMI packet using PacketWriter.
    /// TEST MODE (all S2C UNENC): the payload is materialized then sent via the internal/unencrypted
    /// transport (opcode 0x01). See <see cref="SendRmiAsync(ushort, ReadOnlyMemory{byte}, CancellationToken)"/>
    /// for the rationale (the client has no per-RMI encryption gate; handshake unaffected; revertible).
    /// </summary>
    public async Task SendRmiAsync(ushort rmiId, Action<PacketWriter> writeAction, CancellationToken ct = default)
    {
        if (!IsConnected)
            return;

        byte[] payload;
        using (var payloadWriter = new PacketWriter())
        {
            writeAction(payloadWriter);
            payload = payloadWriter.GetWrittenSpan().ToArray();
        }

        await SendInternalRmiAsync(rmiId, payload, ct);
    }

    /// <summary>
    /// Send an internal ProudNet RMI message (opcode 0x01)
    /// Used for ProudNet internal RMIs like 1108 (NotifyJoinServerComplete)
    /// Format: [Frame][0x01][RmiId:2 LE][Payload]
    ///
    /// Protocol notes:
    /// - Opcode 0x01 is used for internal RMI messages (1001, 1006, 1108, etc.)
    /// - Client sends RMI 1001 (ready signal) using opcode 0x01
    /// - Server sends RMI 1108 (NotifyJoinServerComplete) using opcode 0x01
    /// </summary>
    public async Task SendInternalRmiAsync(ushort rmiId, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        if (!IsConnected)
            return;

        await _sendLock.WaitAsync(ct);
        try
        {
            // Build internal RMI message: [opcode:0x01][RmiId:2 LE][Payload]
            var internalData = new byte[RmiHeaderSize + payload.Length];
            internalData[0] = MessageTypeRmi; // 0x01 - the RMI message type (ProudCoreOpCode.Rmi)
            BinaryPrimitives.WriteUInt16LittleEndian(internalData.AsSpan(RmiIdOffset), rmiId);
            if (!payload.IsEmpty)
                payload.Span.CopyTo(internalData.AsSpan(RmiHeaderSize));

            // Wrap in ProudNet frame
            var frame = ProudFrameCodec.EncodeFrame(internalData);

            // Guarded here too: the interpolated detail would otherwise be built on every send.
            if (_logger.IsEnabled(LogLevel.Debug))
                LogPacket("<<<", frame, $"InternalRMI:{rmiId}");

            await _socket.SendAsync(frame, SocketFlags.None, ct);
            LastActivity = DateTime.UtcNow;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// Send a ProudNet core message (for handshake, etc.)
    /// </summary>
    public async Task SendCoreMessageAsync(ProudCoreOpCode opcode, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        if (!IsConnected)
            return;

        await _sendLock.WaitAsync(ct);
        try
        {
            var frame = ProudFrameCodec.EncodeFrame(opcode, payload.Span);

            if (_logger.IsEnabled(LogLevel.Debug))
                LogPacket("<<<", frame, $"Core:{opcode}");

            await _socket.SendAsync(frame, SocketFlags.None, ct);
            LastActivity = DateTime.UtcNow;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// Send a game RMI packet (for login server RMI like 12001, 22001, etc.)
    /// The payload should already include the RMI ID as first 2 bytes
    /// Format sent: [0x25][flags1:0][flags2:0][len:1][payload with RMI ID]
    /// </summary>
    public async Task SendGameRmiAsync(ReadOnlyMemory<byte> payloadWithRmiId, CancellationToken ct = default)
    {
        if (!IsConnected)
            return;

        await _sendLock.WaitAsync(ct);
        try
        {
            var rmiData = BuildRmiMessage(EncryptModeNone, payloadWithRmiId.Span);

            // Wrap in ProudNet frame
            var frame = ProudFrameCodec.EncodeFrame(rmiData);

            LogPacket("<<<", frame, "GameRMI");

            await _socket.SendAsync(frame, SocketFlags.None, ct);
            LastActivity = DateTime.UtcNow;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// Send a raw frame (data already includes opcode and any sync bytes)
    /// Used for CMessage-style encoding with sync bytes
    /// </summary>
    public async Task SendRawFrameAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (!IsConnected)
            return;

        await _sendLock.WaitAsync(ct);
        try
        {
            // Encode just the frame wrapper (magic + length) around the data
            var frame = ProudFrameCodec.EncodeFrame(data.Span);

            LogPacket("<<<", frame);

            await _socket.SendAsync(frame, SocketFlags.None, ct);
            LastActivity = DateTime.UtcNow;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// Send raw bytes directly to the socket without any framing
    /// Used for Flash cross-domain policy which must be sent raw
    /// </summary>
    public async Task SendRawBytesAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (!IsConnected)
            return;

        await _sendLock.WaitAsync(ct);
        try
        {
            LogPacket("<<<", data.Span, "RawBytes");

            await _socket.SendAsync(data, SocketFlags.None, ct);
            LastActivity = DateTime.UtcNow;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// Disconnect the session
    /// </summary>
    public async Task DisconnectAsync()
    {
        if (_disposed)
            return;

        _cts?.Cancel();

        try
        {
            _socket.Shutdown(SocketShutdown.Both);
        }
        catch { }

        if (_receiveTask != null)
        {
            try
            {
                await _receiveTask.WaitAsync(DisconnectWait);
            }
            catch { }
        }
    }

    private async Task OnDisconnectedAsync()
    {
        State = SessionState.Disconnected;

        if (Disconnected != null)
        {
            try
            {
                await Disconnected.Invoke(this);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Session {SessionId}: Error in disconnect handler", SessionId);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        _cts?.Cancel();

        try
        {
            _socket.Close();
            _socket.Dispose();
        }
        catch { }

        _cts?.Dispose();
        _sendLock.Dispose();
        Crypto.Dispose();

        if (_receiveTask != null)
        {
            try
            {
                await _receiveTask.WaitAsync(DisposeWait);
            }
            catch { }
        }
    }

    /// <summary>
    /// The per-frame and per-packet log lines, source-generated. Each one checks the level before
    /// touching its arguments and never boxes them (a LogDebug call builds a params object[] and
    /// boxes every value type first, on every packet, even with Debug off). Templates, levels and
    /// property names are the ones the LogDebug calls used. The hex lines take a string built only
    /// behind an IsEnabled guard at the call site, hence SkipEnabledCheck.
    /// </summary>
    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Debug, Message = "{Dir} Len:{Len}{Op} {Hex}", SkipEnabledCheck = true)]
        public static partial void Packet(ILogger logger, string dir, int len, string op, string hex);

        [LoggerMessage(Level = LogLevel.Debug, Message = "{Dir} Len:{Len}{Op} {Detail} {Hex}", SkipEnabledCheck = true)]
        public static partial void PacketWithDetail(ILogger logger, string dir, int len, string op, string detail, string hex);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Session {SessionId}: Core message opcode=0x{Opcode:X2}, payload={Len} bytes")]
        public static partial void CoreMessage(ILogger logger, uint sessionId, byte opcode, int len);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Session {SessionId}: RMI flags=0x{F1:X2}{F2:X2}, dataLen={Len}, actual={ActualLen}")]
        public static partial void RmiFlags(ILogger logger, uint sessionId, byte f1, byte f2, int len, int actualLen);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Session {SessionId}: Encrypted data ({Len} bytes): {Data}", SkipEnabledCheck = true)]
        public static partial void EncryptedData(ILogger logger, uint sessionId, int len, string data);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Session {SessionId}: Decrypted data ({Len} bytes): {Data}", SkipEnabledCheck = true)]
        public static partial void DecryptedData(ILogger logger, uint sessionId, int len, string data);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Session {SessionId}: Decrypted RMI id={RmiId} (0x{RmiIdHex:X4}), payload={Len} bytes")]
        public static partial void DecryptedRmi(ILogger logger, uint sessionId, ushort rmiId, ushort rmiIdHex, int len);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Session {SessionId}: RC4 encrypted data ({Len} bytes): {Data}", SkipEnabledCheck = true)]
        public static partial void Rc4EncryptedData(ILogger logger, uint sessionId, int len, string data);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Session {SessionId}: RC4 decrypted data ({Len} bytes): {Data}", SkipEnabledCheck = true)]
        public static partial void Rc4DecryptedData(ILogger logger, uint sessionId, int len, string data);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Session {SessionId}: RC4 decrypted RMI id={RmiId} (0x{RmiIdHex:X4}), payload={Len} bytes")]
        public static partial void Rc4DecryptedRmi(ILogger logger, uint sessionId, ushort rmiId, ushort rmiIdHex, int len);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Session {SessionId}: ProudNet internal message: {Opcode}")]
        public static partial void InternalMessage(ILogger logger, uint sessionId, ProudCoreOpCode opcode);

        // --- compressed (0x27) and counter-less encrypted (0x26) messages ---------------------------

        [LoggerMessage(Level = LogLevel.Debug, Message = "Session {SessionId}: Compressed message (0x27), {Deflated} -> {Inflated} bytes")]
        public static partial void Compressed(ILogger logger, uint sessionId, int deflated, int inflated);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Session {SessionId}: Compressed RMI id={RmiId} (0x{RmiIdHex:X4}), payload={Len} bytes")]
        public static partial void CompressedRmi(ILogger logger, uint sessionId, ushort rmiId, ushort rmiIdHex, int len);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Session {SessionId}: Dropping compressed message (0x27): {Reason}")]
        public static partial void CompressedDropped(ILogger logger, uint sessionId, string reason);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Session {SessionId}: Dropping compressed message (0x27) nested inside another; the client never nests them")]
        public static partial void NestedCompressedDropped(ILogger logger, uint sessionId);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Session {SessionId}: Dropping compressed message that inflated to type 0x{Type:X2} ({Len} bytes), not an RMI (0x01)")]
        public static partial void CompressedNotRmiDropped(ILogger logger, uint sessionId, byte type, int len);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Session {SessionId}: Dropping decrypted message of type 0x{Type:X2} ({Len} bytes): only RMI (0x01) and compressed (0x27) messages are dispatched")]
        public static partial void EncryptedNotRmiDropped(ILogger logger, uint sessionId, byte type, int len);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Session {SessionId}: Dropping message 0x{Type:X2} with encrypt mode 0x{Mode:X2} ({Len} bytes): it is only ever sent encrypted (AES 0x01 / RC4 0x02)")]
        public static partial void UnknownEncryptModeDropped(ILogger logger, uint sessionId, byte type, byte mode, int len);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Session {SessionId}: Dropping message 0x{Type:X2} (mode 0x{Mode:X2}, width byte 0x{Width:X2}): malformed length header")]
        public static partial void EncryptedHeaderDropped(ILogger logger, uint sessionId, byte type, byte mode, byte width);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Session {SessionId}: Dropping message 0x{Type:X2}: declared length {Declared} but {Actual} bytes follow")]
        public static partial void EncryptedLengthDropped(ILogger logger, uint sessionId, byte type, int declared, int actual);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Session {SessionId}: Dropping RMI {RmiId} before the ProudNet handshake completed (state {State})")]
        public static partial void RmiBeforeHandshakeDropped(ILogger logger, uint sessionId, ushort rmiId, HandshakeState state);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Session {SessionId}: Aborting the handshake: {Reason}")]
        public static partial void HandshakeAborted(ILogger logger, uint sessionId, string reason);
    }
}
