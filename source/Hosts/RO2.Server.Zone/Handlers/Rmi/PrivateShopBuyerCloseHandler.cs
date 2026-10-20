using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1484 ReqClosePrivateShopBuyerUI: closes the buyer view of a private shop.</summary>
/// <remarks>
/// The request has no body and receives no answer. It ends the player's watch on the shop, which decides
/// who receives later shop item updates.
/// </remarks>
public class PrivateShopBuyerCloseHandler : ZoneRmiHandlerBase<PrivateShopBuyerCloseHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqClosePrivateShopBuyerUI;
    public override string Name => "PrivateShopBuyerClose";
    public PrivateShopBuyerCloseHandler(IServiceScopeFactory s, ILogger<PrivateShopBuyerCloseHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        Logger.LogDebug("{Name} closes private shop buyer UI", player.Name);
        PrivateShopRegistry.StopWatching(player); 
        return Task.CompletedTask; 
    }
}
