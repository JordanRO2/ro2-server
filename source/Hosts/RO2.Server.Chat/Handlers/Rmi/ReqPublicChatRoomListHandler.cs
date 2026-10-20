using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Network.Handlers;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;

using BasePubRoom = RO2.Network.Handlers.ChatRmiHandlerBase<RO2.Server.Chat.Handlers.Rmi.ReqPublicChatRoomListHandler>;

namespace RO2.Server.Chat.Handlers.Rmi;

/// <summary>Handles RMI 23009 ReqPublicChatRoomList: replies with 23513, the default community channel tabs.</summary>
public class ReqPublicChatRoomListHandler : BasePubRoom
{
    public override ushort Opcode => ChatC2SOpcodes.ReqPublicChatRoomList;
    public override string Name => "ReqPublicChatRoomList";
    public ReqPublicChatRoomListHandler(IServiceScopeFactory s, ILogger<ReqPublicChatRoomListHandler> l) : base(s, l) { }
    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, IServiceProvider services, CancellationToken ct)
    {
        Logger.LogDebug("Session {Id}: {Name}", session.SessionId, Name);
        
        
        
        
        IReadOnlyDictionary<ushort, uint> colors = new Dictionary<ushort, uint>();
        if (session.CharacterId is uint ownerId)
            colors = await services.GetRequiredService<IChannelColorRepository>().GetColorsAsync(ownerId, ct);
        await session.SendInternalRmiAsync(ChatS2COpcodes.AnsPublicChatRoomList, CommunityChannels.BuildChannelListBody(colors));
    }
}
