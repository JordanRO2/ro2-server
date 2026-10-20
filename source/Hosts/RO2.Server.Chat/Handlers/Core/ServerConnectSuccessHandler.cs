using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using RO2.Network.Handlers;
using RO2.Network.ProudNet;

namespace RO2.Server.Chat.Handlers.Core;

/// <summary>
/// Handler for ProudNet 0x32 - Server Connect Success
/// </summary>
public class ServerConnectSuccessHandler : ChatCoreHandlerBase<ServerConnectSuccessHandler>
{
    public override ProudCoreOpCode Opcode => ProudCoreOpCode.NotifyServerConnectSuccess;
    public override string Name => "ServerConnectSuccess";

    public ServerConnectSuccessHandler(ILogger<ServerConnectSuccessHandler> logger) : base(logger) { }

    protected override Task HandleCoreAsync(
        ProudSession session,
        ReadOnlyMemory<byte> payload,
        CancellationToken ct)
    {
        Logger.LogDebug("Session {Id}: Received 0x32 (ServerConnectSuccess)", session.SessionId);
        session.Handshake = HandshakeState.Connected;
        return Task.CompletedTask;
    }
}
