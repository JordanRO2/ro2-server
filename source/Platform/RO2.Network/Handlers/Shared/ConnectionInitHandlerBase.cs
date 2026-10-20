using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.ProudNet;

namespace RO2.Network.Handlers.Shared;

/// <summary>
/// Shared handler for ProudNet 0x2F - Connection Init.
/// Sends Flash policy + RSA PeerAccept. Identical for login and zone.
/// </summary>
public abstract class ConnectionInitHandlerBase<THandler> : CoreHandlerBase<THandler>
    where THandler : class
{
    /// <summary>Flash socket policy file answer, NUL-terminated: allows any domain on any port.</summary>
    private const string FlashPolicy = "<?xml version=\"1.0\"?><cross-domain-policy><allow-access-from domain=\"*\" to-ports=\"*\" /></cross-domain-policy>\0";

    /// <summary>Size of the leading client flags word (u32) in the 0x2F request payload.</summary>
    private const int ClientFlagsLength = 4;

    /// <summary>
    /// Opcode of the answer (<see cref="ProudCoreOpCode.NotifyServerConnectionHint"/>, 0x04). It is written
    /// as a 32-bit little-endian value, not a single byte.
    /// </summary>
    private const int ConnectionHintOpcode = (int)ProudCoreOpCode.NotifyServerConnectionHint;

    /// <summary>Session timeout announced to the client, in milliseconds (10 minutes).</summary>
    private const int SessionTimeoutMs = 600000;

    /// <summary>Second timeout value announced to the client.</summary>
    private const int SecondaryTimeout = 60;

    /// <summary>Length of the RSA key the client encrypts its session key with, in bytes (RSA-1024).</summary>
    private const int RsaKeyLengthBytes = 128;

    /// <summary>Length of the fast (RC4) message key, in bits.</summary>
    private const int FastMessageKeyLengthBits = 512;

    /// <summary>A 32-bit field of the hint that every server sends as 1; its meaning is not established.</summary>
    private const int HintFlagOn = 1;

    /// <summary>Initial capacity of the hint payload: 43 fixed bytes plus the DER public key.</summary>
    private const int HintCapacity = 200;

    // The flag bytes of the hint payload sit between its 32-bit fields. Their meaning is not established; the
    // values are constant across servers and the client expects them verbatim, so each byte is named by its value.

    /// <summary>Flag byte value 0 in a hint flag group.</summary>
    private const byte HintFlagClear = 0x00;

    /// <summary>Flag byte value 1 in a hint flag group.</summary>
    private const byte HintFlagSet = 0x01;

    /// <summary>Last byte of <see cref="HintFlagsD"/>, the only flag byte with the value 2.</summary>
    private const byte HintFlagsDTail = 0x02;

    /// <summary>Hint flags A, five bytes after the opcode: clear, set, then three clear.</summary>
    private static ReadOnlySpan<byte> HintFlagsA =>
        [HintFlagClear, HintFlagSet, HintFlagClear, HintFlagClear, HintFlagClear];

    /// <summary>Hint flags B, two bytes before the session timeout: set, clear.</summary>
    private static ReadOnlySpan<byte> HintFlagsB => [HintFlagSet, HintFlagClear];

    /// <summary>Hint flags C, two bytes after the session timeout: set, clear.</summary>
    private static ReadOnlySpan<byte> HintFlagsC => [HintFlagSet, HintFlagClear];

    /// <summary>Hint flags D, four bytes before the DER length: three clear, then <see cref="HintFlagsDTail"/>.</summary>
    private static ReadOnlySpan<byte> HintFlagsD => [HintFlagClear, HintFlagClear, HintFlagClear, HintFlagsDTail];

    public override ProudCoreOpCode Opcode => ProudCoreOpCode.ConnectionInit;
    public override string Name => "ConnectionInit";

    protected ConnectionInitHandlerBase(ILogger<THandler> logger) : base(logger) { }

    protected override async Task HandleCoreAsync(
        ProudSession session, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        // The handshake runs once, from Initial. A repeated 0x2F used to redo the key generation and the
        // policy send on every copy (S-H6); now it changes nothing and gets no answer.
        if (session.Handshake != HandshakeState.Initial)
        {
            Logger.LogWarning("Session {SessionId}: ConnectionInit (0x2F) ignored in handshake state {State}",
                session.SessionId, session.Handshake);
            return;
        }

        // Taken before the first await so no later frame can see Initial again.
        session.Handshake = HandshakeState.WaitingForSessionKey;

        // The hex was built as a LogDebug argument, i.e. whether or not Debug is on.
        if (Logger.IsEnabled(LogLevel.Debug))
            ConnectionInitLog.ConnectionInit(Logger, session.SessionId, Convert.ToHexString(payload.Span));

        if (payload.Length >= ClientFlagsLength)
        {
            var clientFlags = BinaryPrimitives.ReadUInt32LittleEndian(payload.Span);
            ConnectionInitLog.ConnectionFlags(Logger, session.SessionId, clientFlags);
        }

        var policyBytes = System.Text.Encoding.ASCII.GetBytes(FlashPolicy);
        await session.SendRawBytesAsync(policyBytes);

        session.Crypto.GenerateKeyPair();
        var rsaPublicKeyDer = session.Crypto.PublicKeyDer;

        await session.SendRawFrameAsync(BuildPeerAcceptPayload(rsaPublicKeyDer));
    }

    /// <summary>
    /// Builds the 0x04 NotifyServerConnectionHint payload (legacy name PeerAccept):
    /// <c>[i32 opcode][5 flag bytes][2 flag bytes][i32 timeout ms][2 flag bytes][i32 second timeout]
    /// [i32 RSA key bytes][i32 fast key bits][i32 1][i32 1][4 flag bytes][u16 DER length][DER public key]</c>.
    /// </summary>
    private static byte[] BuildPeerAcceptPayload(byte[] rsaPublicKeyDer)
    {
        using var pw = new PacketWriter(HintCapacity);

        pw.WriteInt32(ConnectionHintOpcode);
        pw.WriteBytes(HintFlagsA);
        pw.WriteBytes(HintFlagsB);
        pw.WriteInt32(SessionTimeoutMs);
        pw.WriteBytes(HintFlagsC);
        pw.WriteInt32(SecondaryTimeout);
        pw.WriteInt32(RsaKeyLengthBytes);
        pw.WriteInt32(FastMessageKeyLengthBits);
        pw.WriteInt32(HintFlagOn);
        pw.WriteInt32(HintFlagOn);
        pw.WriteBytes(HintFlagsD);
        pw.WriteInt16((short)rsaPublicKeyDer.Length);
        pw.WriteBytes(rsaPublicKeyDer);

        return pw.ToArray();
    }
}

/// <summary>
/// ConnectionInit's Debug lines, source-generated (same templates, levels and property names as
/// the LogDebug calls). Outside the generic base so one definition serves the Login, Zone and Chat
/// handlers; the category stays the handler's own because its logger is passed in.
/// </summary>
internal static partial class ConnectionInitLog
{
    // The caller builds the hex behind its own IsEnabled check, so the generated one is skipped.
    [LoggerMessage(Level = LogLevel.Debug, Message = "Session {SessionId}: ConnectionInit - {Hex}", SkipEnabledCheck = true)]
    internal static partial void ConnectionInit(ILogger logger, uint sessionId, string hex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Session {SessionId}: Connection flags: 0x{Flags:X8}")]
    internal static partial void ConnectionFlags(ILogger logger, uint sessionId, uint flags);
}
