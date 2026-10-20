using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using RO2.Network.Handlers;
using RO2.Network.ProudNet;

namespace RO2.Server.Zone.Handlers.Core;

/// <summary>
/// Handler for ProudNet core type 0x32 (<see cref="ProudCoreOpCode.NotifyServerConnectSuccess"/>) on the Zone
/// connection: the client's reply to the server's connect-success, optionally carrying a little-endian i64 send
/// timestamp (Unix milliseconds) from which the handler logs a latency. It marks the handshake complete and is
/// not answered.
/// </summary>
public class ServerConnectSuccessHandler : ZoneCoreHandlerBase<ServerConnectSuccessHandler>
{
    /// <summary>Size of the optional timestamp at the start of the payload: an i64.</summary>
    private const int TimestampLength = sizeof(long);

    public override ProudCoreOpCode Opcode => ProudCoreOpCode.NotifyServerConnectSuccess;
    public override string Name => "ServerConnectSuccess";

    public ServerConnectSuccessHandler(ILogger<ServerConnectSuccessHandler> logger) : base(logger)
    {
    }

    protected override Task HandleCoreAsync(
        ProudSession session,
        ReadOnlyMemory<byte> payload,
        CancellationToken ct)
    {
        Logger.LogDebug("Session {SessionId}: Received 0x32 (ServerConnectSuccess) - {Size} bytes",
            session.SessionId, payload.Length);

        if (payload.Length >= TimestampLength)
        {
            var clientTimestamp = BinaryPrimitives.ReadInt64LittleEndian(payload.Span);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Logger.LogDebug("Session {SessionId}: Latency: {Latency}ms", session.SessionId, now - clientTimestamp);
        }

        session.Handshake = HandshakeState.Connected;
        return Task.CompletedTask;
    }
}
