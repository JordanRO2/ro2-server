using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Handlers;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;

using BaseGroup = RO2.Network.Handlers.ChatRmiHandlerBase<RO2.Server.Chat.Handlers.Rmi.ReqGroupListHandler>;

namespace RO2.Server.Chat.Handlers.Rmi;

/// <summary>Handles RMI 23004 ReqGroupList: replies with 23507 AnsGroupList. The reply is always the empty list.</summary>
/// <remarks>
/// No group state is modeled. The reply body is the fixed empty list sent by <see cref="ChatHelper.SendEmptyList"/>.
/// </remarks>
public class ReqGroupListHandler : BaseGroup
{
    public override ushort Opcode => ChatC2SOpcodes.ReqGroupList;
    public override string Name => "ReqGroupList";
    public ReqGroupListHandler(IServiceScopeFactory s, ILogger<ReqGroupListHandler> l) : base(s, l) { }
    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, IServiceProvider services, CancellationToken ct)
    {
        Logger.LogDebug("Session {Id}: {Name}", session.SessionId, Name);
        
        
        
        await ChatHelper.SendEmptyList(session, ChatS2COpcodes.AnsGroupList);
    }
}
