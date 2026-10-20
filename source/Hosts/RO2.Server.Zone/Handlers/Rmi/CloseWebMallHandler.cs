using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1579 ReqCloseWebMall: closes the web mall window.</summary>
public class CloseWebMallHandler : ZoneRmiHandlerBase<CloseWebMallHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqCloseWebMall;
    public override string Name => "CloseWebMall";
    public CloseWebMallHandler(IServiceScopeFactory s, ILogger<CloseWebMallHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        Logger.LogDebug("{Name} closes web mall", player.Name);
        
        return Task.CompletedTask;
    }
}
