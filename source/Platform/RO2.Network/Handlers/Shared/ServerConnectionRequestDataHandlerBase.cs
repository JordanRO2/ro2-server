using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using RO2.Network.ProudNet;

namespace RO2.Network.Handlers.Shared;

/// <summary>
/// Shared handler for the ProudNet ServerConnectionRequestData message. Replies with the host id, protocol version and OK. Used by the login and zone servers.
/// </summary>
public abstract class ServerConnectionRequestDataHandlerBase<THandler> : CoreHandlerBase<THandler>
    where THandler : class
{
    public override ProudCoreOpCode Opcode => ProudCoreOpCode.NotifyServerConnectionRequestData;
    public override string Name => "ServerConnectionRequestData";

    protected ServerConnectionRequestDataHandlerBase(ILogger<THandler> logger) : base(logger) { }

    protected override async Task HandleCoreAsync(
        ProudSession session, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        Logger.LogDebug("Session {Id}: Connection request data ({Len} bytes)", session.SessionId, payload.Length);

        session.Handshake = HandshakeState.Connected;

        var responseData = new byte[9];
        BinaryPrimitives.WriteUInt32LittleEndian(responseData, session.SessionId);
        BinaryPrimitives.WriteUInt32LittleEndian(responseData.AsSpan(4), ProudConstants.NetVersion);
        responseData[8] = 1;

        await session.SendCoreMessageAsync(ProudCoreOpCode.NotifyServerConnectSuccess, responseData);

        Logger.LogInformation("Session {Id}: ProudNet handshake complete, HostId: {HostId}",
            session.SessionId, session.SessionId);
    }
}
