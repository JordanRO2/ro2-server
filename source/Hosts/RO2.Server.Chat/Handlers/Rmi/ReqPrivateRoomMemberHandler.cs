using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Handlers;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;

namespace RO2.Server.Chat.Handlers.Rmi;

/// <summary>
/// Handles RMI 23017 ReqPrivateRoomInMember: returns the member list of a private room.
/// Replies with 23517 AnsPrivateRoomInMember for the requested room GUID, in the same shape as the join answer
/// (see <see cref="PrivateChatRooms"/>).
/// </summary>
/// <remarks>
/// Request body: the bare 16-byte room GUID. No reply is sent for a GUID that is not an existing room, since this
/// reply defines no failure shape.
/// </remarks>
public class ReqPrivateRoomMemberHandler : ChatRmiHandlerBase<ReqPrivateRoomMemberHandler>
{
    public override ushort Opcode => ChatC2SOpcodes.ReqPrivateRoomInMember; 
    public override string Name => "RoomMembers";
    public ReqPrivateRoomMemberHandler(IServiceScopeFactory s, ILogger<ReqPrivateRoomMemberHandler> l) : base(s, l) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length != 16)
        {
            Logger.LogWarning("Session {Id}: 23017 body is {Len} bytes (retail: 16)", session.SessionId, payload.Length);
            return;
        }

        var sends = new List<PrivateChatRooms.Send>();
        if (!PrivateChatRooms.MemberList(payload.Span, session, sends))
            Logger.LogDebug("Session {Id}: 23017 for unknown room {Guid}", session.SessionId, Convert.ToHexString(payload.Span));
        await PrivateChatRooms.DeliverAsync(sends);
    }
}
