using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using RO2.Network.ProudNet;

namespace RO2.Network.Handlers.Shared;

/// <summary>
/// Shared handler for the legacy encrypted-session-key notification (client-to-server core opcode 0x10).
/// Advances the handshake to <see cref="HandshakeState.WaitingForConnectionRequest"/> and replies with core
/// opcode 0x31 carrying the server clock as an 8-byte little-endian Unix time in milliseconds. Identical for
/// login and zone.
/// </summary>
public abstract class LegacySessionKeyHandlerBase<THandler> : CoreHandlerBase<THandler>
    where THandler : class
{
    /// <summary>Size of the reply payload: one little-endian int64 (server Unix time, milliseconds).</summary>
    private const int ServerTimeReplySize = sizeof(long);

    public override ProudCoreOpCode Opcode => ProudCoreOpCode.NotifyCSEncryptedSessionKey;
    public override string Name => "LegacySessionKey";

    protected LegacySessionKeyHandlerBase(ILogger<THandler> logger) : base(logger) { }

    protected override async Task HandleCoreAsync(
        ProudSession session, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        Logger.LogDebug("Session {Id}: Legacy session key ({Len} bytes)", session.SessionId, payload.Length);

        session.Handshake = HandshakeState.WaitingForConnectionRequest;

        var encKeyPayload = new byte[ServerTimeReplySize];
        BinaryPrimitives.WriteInt64LittleEndian(encKeyPayload, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await session.SendCoreMessageAsync(ProudCoreOpCode.NotifyCSSessionKeySuccess, encKeyPayload);
    }
}
