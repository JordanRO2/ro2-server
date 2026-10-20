using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Handlers;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;

namespace RO2.Server.Chat.Handlers.Rmi;

/// <summary>Handles RMI 23010 ReqPrivateChatRoomList: replies with 23514 AnsPrivateChatRoomList, always the empty list.</summary>
/// <remarks>Private rooms are not listed. The reply is the fixed empty list sent by <see cref="ChatHelper.SendEmptyList"/>.</remarks>
public class ReqPrivateChatRoomListHandler : ChatRmiHandlerBase<ReqPrivateChatRoomListHandler>
{
    public override ushort Opcode => ChatC2SOpcodes.ReqPrivateChatRoomList; 
    public override string Name => "PrivateRoomList";
    public ReqPrivateChatRoomListHandler(IServiceScopeFactory s, ILogger<ReqPrivateChatRoomListHandler> l) : base(s, l) { }
    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, IServiceProvider services, CancellationToken ct)
    {
        Logger.LogDebug("Session {Id}: {Name}", session.SessionId, Name);
        
        
        await ChatHelper.SendEmptyList(session, ChatS2COpcodes.AnsPrivateChatRoomList);
    }
}
