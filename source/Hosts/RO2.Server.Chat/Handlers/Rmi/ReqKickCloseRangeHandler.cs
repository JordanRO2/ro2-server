using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Handlers;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;

namespace RO2.Server.Chat.Handlers.Rmi;

/// <summary>Handles RMI 23021 ReqKickCloseRangeChatRoom. Currently a no-op.</summary>
public class ReqKickCloseRangeHandler : ChatRmiHandlerBase<ReqKickCloseRangeHandler>
{
    public override ushort Opcode => ChatC2SOpcodes.ReqKickCloseRangeChatRoom; 
    public override string Name => "KickCloseRange";
    public ReqKickCloseRangeHandler(IServiceScopeFactory s, ILogger<ReqKickCloseRangeHandler> l) : base(s, l) { }
    protected override Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, IServiceProvider services, CancellationToken ct)
    { Logger.LogDebug("Session {Id}: {Name}", session.SessionId, Name); return Task.CompletedTask; }
}
