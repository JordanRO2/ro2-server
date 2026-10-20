using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Handlers;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;

namespace RO2.Server.Chat.Handlers.Rmi;

/// <summary>
/// Handles RMI 23013 ReqChatRoomExit: removes the sender from a chat room and replies with 23519 AnsChatRoomExit.
/// </summary>
/// <remarks>
/// Request body: [u16 tabIndex][16-byte room GUID]. The index identifies a private room or a community channel.
/// The reply echoes the GUID with result 0 and is sent even if the sender was not a member. Remaining members
/// receive 23520 NfyChatRoomExitMemberInfo, and 23533 is sent when the master leaves.
/// Leaving a community channel also removes the session from <see cref="CommunityChannelMembership.Leave"/>,
/// so the session stops receiving that channel's lines.
/// </remarks>
public class ReqChatRoomExitHandler : ChatRmiHandlerBase<ReqChatRoomExitHandler>
{
    public override ushort Opcode => ChatC2SOpcodes.ReqChatRoomExit; 
    public override string Name => "RoomExit";
    public ReqChatRoomExitHandler(IServiceScopeFactory s, ILogger<ReqChatRoomExitHandler> l) : base(s, l) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length != 18)
        {
            Logger.LogWarning("Session {Id}: 23013 body is {Len} bytes (retail: 18)", session.SessionId, payload.Length);
            return;
        }
        var roomGuid = payload.Slice(2, 16);

        
        if (CommunityChannels.ByGuid(roomGuid.Span) is { } channel)
        {
            bool wasIn = CommunityChannelMembership.Leave(session, channel.Index);
            Logger.LogInformation("Session {Id}: {Who} leaves community channel {Channel}{Note}", session.SessionId,
                session.Username ?? "?", channel.Name, wasIn ? "" : " (had already left it)");
            
            await session.SendInternalRmiAsync(ChatS2COpcodes.AnsChatRoomExit, PrivateChatRooms.BuildExitAnswer(roomGuid.Span));
            return;
        }

        var sends = new List<PrivateChatRooms.Send>();
        bool wasMember = session.CharacterId is uint uid && PrivateChatRooms.Leave(roomGuid.Span, uid, sends);
        Logger.LogInformation("Session {Id}: {Who} leaves private chat room {Guid}{Note}", session.SessionId,
            session.Username ?? "?", Convert.ToHexString(roomGuid.Span), wasMember ? "" : " (was not a member)");

        await session.SendInternalRmiAsync(ChatS2COpcodes.AnsChatRoomExit, PrivateChatRooms.BuildExitAnswer(roomGuid.Span));
        await PrivateChatRooms.DeliverAsync(sends);
    }
}
