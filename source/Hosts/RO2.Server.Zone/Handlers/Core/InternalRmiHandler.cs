using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using RO2.Network.Handlers;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;

namespace RO2.Server.Zone.Handlers.Core;

/// <summary>
/// Handler for ProudNet core type 0x01 (<see cref="ProudCoreOpCode.Rmi"/>) on the Zone connection: the
/// ProudNet framework RMIs (ids 1001-1019, below the game's RMI ids) the client sends as <c>[u16 rmiId][body]</c>.
/// They are only logged; none is answered.
/// </summary>
public class InternalRmiHandler : ZoneCoreHandlerBase<InternalRmiHandler>
{
    /// <summary>Size of the RMI id that opens every payload.</summary>
    private const int RmiIdLength = sizeof(ushort);

    public override ProudCoreOpCode Opcode => ProudCoreOpCode.Rmi;
    public override string Name => "InternalRmi";

    public InternalRmiHandler(ILogger<InternalRmiHandler> logger) : base(logger)
    {
    }

    protected override async Task HandleCoreAsync(
        ProudSession session,
        ReadOnlyMemory<byte> payload,
        CancellationToken ct)
    {
        if (payload.Length >= RmiIdLength)
        {
            var msgId = BinaryPrimitives.ReadUInt16LittleEndian(payload.Span);

            switch (msgId)
            {
                case ProudNetC2SOpcodes.ClientReady:
                    // The zone greeting was already sent from ClientConnectInfoHandler.
                    Logger.LogDebug("Session {SessionId}: Client ready (RMI 1001) - acknowledged",
                        session.SessionId);
                    break;

                case ProudNetC2SOpcodes.P2PGroup_MemberJoined:
                    Logger.LogInformation("Session {SessionId}: P2P group joined (RMI 1006)",
                        session.SessionId);
                    break;

                case ProudNetC2SOpcodes.NotifyConnectionPeerReady:
                    // Id 1019: the client's periodic keepalive (~30 s); never answered.
                    Logger.LogTrace("Session {SessionId}: keepalive (internal RMI 1019)", session.SessionId);
                    break;

                case ProudNetC2SOpcodes.NotifyCSSessionKey:
                    Logger.LogDebug("Session {SessionId}: NotifyCSSessionKey (RMI 1002)", session.SessionId);
                    break;

                case ProudNetC2SOpcodes.NotifyCSP2PSessionKey:
                    Logger.LogDebug("Session {SessionId}: NotifyCSP2PSessionKey (RMI 1004) - no P2P, ignored", session.SessionId);
                    break;

                case ProudNetC2SOpcodes.ShutdownTcp:
                    Logger.LogInformation("Session {SessionId}: ShutdownTcp (RMI 1012) - client disconnecting gracefully", session.SessionId);
                    break;

                case ProudNetC2SOpcodes.NotifyJitDirectP2PTriggered:
                    Logger.LogDebug("Session {SessionId}: NotifyJitDirectP2PTriggered (RMI 1017) - no P2P, ignored", session.SessionId);
                    break;

                case ProudNetC2SOpcodes.NotifySuperPeerAuth:
                    Logger.LogDebug("Session {SessionId}: NotifySuperPeerAuth (RMI 1018) - no super-peer, ignored", session.SessionId);
                    break;

                default:
                    // Unknown framework RMI: log its id and body hex so it can be identified later.
                    var body = payload.Length > RmiIdLength ? Convert.ToHexString(payload.Span[RmiIdLength..]) : "";
                    Logger.LogWarning("[UNHANDLED-INTERNAL-RMI] {MsgId} session={SessionId} len={Len} data={Data}",
                        msgId, session.SessionId, payload.Length, body);
                    break;
            }
        }
    }
}
