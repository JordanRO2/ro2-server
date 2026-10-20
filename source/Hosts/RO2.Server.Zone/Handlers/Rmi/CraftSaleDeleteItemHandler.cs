using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles RMI 1447 ReqCraftSaleDeleteItem: removes an item from the player's craft sale.</summary>
public class CraftSaleDeleteItemHandler : ZoneRmiHandlerBase<CraftSaleDeleteItemHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqCraftSaleDeleteItem;
    public override string Name => "CraftSaleDeleteItem";
    public CraftSaleDeleteItemHandler(IServiceScopeFactory s, ILogger<CraftSaleDeleteItemHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        Logger.LogInformation("{Name} removes craft sale item", player.Name);
        
        await session.SendRmiAsync(ZoneS2COpcodes.OnAckCraftSaleDeleteItem, writer =>
        {
            writer.WriteUInt16(0); 
        });
    }
}
