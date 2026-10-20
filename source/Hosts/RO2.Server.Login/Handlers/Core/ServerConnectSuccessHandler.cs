using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using RO2.Network.Handlers;
using RO2.Network.ProudNet;

namespace RO2.Server.Login.Handlers.Core;

/// <summary>
/// Handles ProudNet core message 0x32, the client's acknowledgement of the server's 0x31 session-key success:
/// the body echoes the u64 millisecond timestamp the server sent in 0x31. Receiving it completes the handshake.
/// </summary>
/// <remarks>
/// The wire value 0x32 is <see cref="ProudCoreOpCode.P2PReliablePong"/>; this handler binds to it under the
/// alias <see cref="ProudCoreOpCode.NotifyServerConnectSuccess"/>.
/// </remarks>
public class ServerConnectSuccessHandler : LoginCoreHandlerBase<ServerConnectSuccessHandler>
{
    /// <summary>Size of the echoed server timestamp (i64 milliseconds since the Unix epoch).</summary>
    private const int EchoedTimestampLength = sizeof(long);

    public override ProudCoreOpCode Opcode => ProudCoreOpCode.NotifyServerConnectSuccess;
    public override string Name => "ServerConnectSuccess";

    public ServerConnectSuccessHandler(ILogger<ServerConnectSuccessHandler> logger) : base(logger)
    {
    }

    protected override async Task HandleCoreAsync(
        ProudSession session,
        ReadOnlyMemory<byte> payload,
        CancellationToken ct)
    {
        // The client echoes the timestamp from 0x31 under opcode 0x32.
        Logger.LogDebug("Session {SessionId}: Received 0x32 (ServerConnectSuccess) - {Size} bytes",
            session.SessionId, payload.Length);

        // The echoed timestamp gives the round-trip latency.
        if (payload.Length >= EchoedTimestampLength)
        {
            var clientTimestamp = BinaryPrimitives.ReadInt64LittleEndian(payload.Span);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var latency = now - clientTimestamp;
            Logger.LogDebug("Session {SessionId}: Round-trip latency: {Latency}ms", session.SessionId, latency);
        }

        // The echo confirms the session key was accepted: the handshake is complete.
        session.Handshake = HandshakeState.Connected;

        Logger.LogInformation("Session {SessionId}: ProudNet handshake complete, HostId: {HostId}",
            session.SessionId, session.SessionId);

        // RMI 1108 (JoinServerComplete) is deliberately not sent here. The client only acts on it once its game
        // engine has finished starting up; sent earlier it is ignored and the version check (RMI 12001) is
        // never requested. The client announces readiness with RMI 1001 (ClientReady), and the internal RMI
        // handler sends 1108 in answer to that.
        Logger.LogDebug("Session {SessionId}: Waiting for client readiness signal (RMI 1001)", session.SessionId);
    }
}
