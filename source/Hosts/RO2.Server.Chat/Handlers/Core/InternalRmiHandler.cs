using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using RO2.Network.Handlers;
using RO2.Network.ProudNet;

namespace RO2.Server.Chat.Handlers.Core;

/// <summary>
/// Handler for ProudNet 0x01 - Internal RMI (Chat server version)
/// Handles the ProudNet framework RMIs the client sends on the messenger connection: 1001, the
/// 1019 keepalive, and the P2P/shutdown notices. None of them is answered.
/// </summary>
public class InternalRmiHandler : ChatCoreHandlerBase<InternalRmiHandler>
{
    // ProudNet framework RMI ids (the 1001-1019 range is the library's own, below the game's RMI ids).
    private const ushort RmiClientReady = 1001;
    private const ushort RmiNotifyCsSessionKey = 1002;
    private const ushort RmiNotifyCsP2PSessionKey = 1004;
    private const ushort RmiP2PGroupMemberJoined = 1006;
    private const ushort RmiShutdownTcp = 1012;
    private const ushort RmiNotifyJitDirectP2PTriggered = 1017;
    private const ushort RmiNotifySuperPeerAuth = 1018;

    /// <summary>
    /// ProudNet keepalive: the client sends it every ~30 s on each connection with a 4-byte all-zero body and
    /// expects no answer.
    /// </summary>
    private const ushort RmiKeepalive = 1019;

    public override ProudCoreOpCode Opcode => (ProudCoreOpCode)0x01;
    public override string Name => "InternalRmi";

    public InternalRmiHandler(ILogger<InternalRmiHandler> logger) : base(logger) { }

    protected override Task HandleCoreAsync(
        ProudSession session,
        ReadOnlyMemory<byte> payload,
        CancellationToken ct)
    {
        if (payload.Length >= 2)
        {
            var msgId = BinaryPrimitives.ReadUInt16LittleEndian(payload.Span);
            Logger.LogDebug("Session {Id}: Internal RMI {MsgId}", session.SessionId, msgId);

            switch (msgId)
            {
                case RmiKeepalive:
                    // Never answered. Game-mall catalog data (2866 AnsGameMallItemListAll / 2868 AnsGameMallMyInfo)
                    // is sent only on the zone connection, in reply to 1592 ReqGameMallItemListAll /
                    // 1596 ReqGameMallMyInfo, never in response to a keepalive.
                    Logger.LogTrace("Session {Id}: keepalive (internal RMI 1019)", session.SessionId);
                    break;

                case RmiClientReady:
                    Logger.LogDebug("Session {Id}: Client ready (RMI 1001) - acknowledged", session.SessionId);
                    break;

                // ProudNet framework messages (client-server: no real P2P)
                case RmiNotifyCsSessionKey:
                    Logger.LogDebug("Session {Id}: NotifyCSSessionKey (RMI 1002)", session.SessionId);
                    break;

                case RmiNotifyCsP2PSessionKey:
                    Logger.LogDebug("Session {Id}: NotifyCSP2PSessionKey (RMI 1004) - no P2P, ignored", session.SessionId);
                    break;

                case RmiP2PGroupMemberJoined:
                    Logger.LogDebug("Session {Id}: P2P group joined (RMI 1006)", session.SessionId);
                    break;

                case RmiShutdownTcp:
                    Logger.LogInformation("Session {Id}: ShutdownTcp (RMI 1012) - client disconnecting gracefully", session.SessionId);
                    break;

                case RmiNotifyJitDirectP2PTriggered:
                    Logger.LogDebug("Session {Id}: NotifyJitDirectP2PTriggered (RMI 1017) - no P2P, ignored", session.SessionId);
                    break;

                case RmiNotifySuperPeerAuth:
                    Logger.LogDebug("Session {Id}: NotifySuperPeerAuth (RMI 1018) - no super-peer, ignored", session.SessionId);
                    break;

                default:
                    var body = payload.Length > 2 ? Convert.ToHexString(payload.Span[2..]) : "";
                    Logger.LogWarning("[UNHANDLED-INTERNAL-RMI] {MsgId} session={SessionId} len={Len} data={Data}",
                        msgId, session.SessionId, payload.Length, body);
                    break;
            }
        }

        return Task.CompletedTask;
    }
}
