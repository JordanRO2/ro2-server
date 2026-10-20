using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles RMI 1452 ReqCraftSaleShopItemBuy: buys an item from a craft sale shop.</summary>
public class CraftSaleShopItemBuyHandler : ZoneRmiHandlerBase<CraftSaleShopItemBuyHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqCraftSaleShopItemBuy;
    public override string Name => "CraftSaleShopItemBuy";
    public CraftSaleShopItemBuyHandler(IServiceScopeFactory s, ILogger<CraftSaleShopItemBuyHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < 14) return;
        Logger.LogInformation("{Name} buys craft sale item", player.Name);
        
        await session.SendRmiAsync(ZoneS2COpcodes.OnAckCraftSaleShopItemBuy, writer =>
        {
            writer.WriteUInt16(0); 
        });
    }
}
