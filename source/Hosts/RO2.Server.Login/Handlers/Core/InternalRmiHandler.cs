using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using RO2.Network.Handlers;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;

namespace RO2.Server.Login.Handlers.Core;

/// <summary>
/// Handles the ProudNet internal RMI (core message type 0x01, <see cref="ProudCoreOpCode.Rmi"/>) the client sends on the
/// login connection: ClientReady (1001), P2PGroup_MemberJoined (1006) and the other ProudNet framework notices
/// (<see cref="ProudNetC2SOpcodes"/>). None of them is answered; this is a client-server game, so there is no P2P.
/// </summary>
public class InternalRmiHandler : LoginCoreHandlerBase<InternalRmiHandler>
{
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
        Logger.LogDebug("Session {SessionId}: Internal RMI 0x01 ({Size} bytes): {Hex}",
            session.SessionId, payload.Length, Convert.ToHexString(payload.Span));

        if (payload.Length >= 2)
        {
            var msgId = BinaryPrimitives.ReadUInt16LittleEndian(payload.Span);
            var innerPayload = payload.Length > 2 ? payload.Slice(2) : ReadOnlyMemory<byte>.Empty;

            Logger.LogDebug("Session {SessionId}: Internal RMI ID {MsgId}, inner payload: {Size} bytes",
                session.SessionId, msgId, innerPayload.Length);

            // Payload shape: [u16 rmiId][body]. The ids are ProudNet's own framework range (1001-1019 here).
            // Nothing below sends a reply: the join-complete notice (S2C 1108, ProudNetS2COpcodes.JoinServerComplete)
            // already went out during the version check, and repeating it after login resets the client's login mode.
            switch (msgId)
            {
                case ProudNetC2SOpcodes.ClientReady:
                    Logger.LogInformation("Session {SessionId}: Client ready (RMI 1001) - acknowledged",
                        session.SessionId);
                    break;

                case ProudNetC2SOpcodes.P2PGroup_MemberJoined:
                    // The client confirms it joined the P2P group after the join-complete notice. Informational only.
                    Logger.LogInformation("Session {SessionId}: P2P group joined (RMI 1006) - handshake complete, waiting for login credentials",
                        session.SessionId);
                    break;

                // ProudNet framework messages: the connection is client-server, so these are acknowledged and ignored.
                case ProudNetC2SOpcodes.NotifyCSSessionKey: // key setup is handled by the session-key handlers
                    Logger.LogDebug("Session {SessionId}: NotifyCSSessionKey (RMI 1002)", session.SessionId);
                    break;

                case ProudNetC2SOpcodes.NotifyCSP2PSessionKey:
                    Logger.LogDebug("Session {SessionId}: NotifyCSP2PSessionKey (RMI 1004) - no P2P, ignored", session.SessionId);
                    break;

                case ProudNetC2SOpcodes.ShutdownTcp: // graceful TCP shutdown request; the connection closes next
                    Logger.LogInformation("Session {SessionId}: ShutdownTcp (RMI 1012) - client disconnecting gracefully", session.SessionId);
                    break;

                case ProudNetC2SOpcodes.NotifyJitDirectP2PTriggered:
                    Logger.LogDebug("Session {SessionId}: NotifyJitDirectP2PTriggered (RMI 1017) - no P2P, ignored", session.SessionId);
                    break;

                case ProudNetC2SOpcodes.NotifySuperPeerAuth:
                    Logger.LogDebug("Session {SessionId}: NotifySuperPeerAuth (RMI 1018) - no super-peer, ignored", session.SessionId);
                    break;

                default:
                    // Unknown internal RMI: log the id and body hex so it can be identified later.
                    var body = payload.Length > 2 ? Convert.ToHexString(payload.Span[2..]) : "";
                    Logger.LogWarning("[UNHANDLED-INTERNAL-RMI] {MsgId} session={SessionId} len={Len} data={Data}",
                        msgId, session.SessionId, payload.Length, body);
                    break;
            }
        }
    }
}
