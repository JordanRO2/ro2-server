using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles RMI 1446 ReqCraftSaleRegistItem: registers an item for sale in the player's craft sale.</summary>
public class CraftSaleRegistItemHandler : ZoneRmiHandlerBase<CraftSaleRegistItemHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqCraftSaleRegistItem;
    public override string Name => "CraftSaleRegistItem";
    public CraftSaleRegistItemHandler(IServiceScopeFactory s, ILogger<CraftSaleRegistItemHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        Logger.LogInformation("{Name} registers craft sale item", player.Name);
        
        await session.SendRmiAsync(ZoneS2COpcodes.OnAckCraftSaleRegistItem, writer =>
        {
            writer.WriteUInt16(0); 
        });
    }
}
