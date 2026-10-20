using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles RMI 1448 ReqCraftSaleOpenShop: opens the player's craft sale shop.</summary>
public class CraftSaleOpenShopHandler : ZoneRmiHandlerBase<CraftSaleOpenShopHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqCraftSaleOpenShop;
    public override string Name => "CraftSaleOpenShop";
    public CraftSaleOpenShopHandler(IServiceScopeFactory s, ILogger<CraftSaleOpenShopHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < 42) return;
        var reader = new PacketReader(payload.Span);
        
        Logger.LogInformation("{Name} opens craft sale shop", player.Name);

        
        
        
        
        await session.SendRmiAsync(ZoneS2COpcodes.OnAckCraftSaleOpenShop, w =>
        {
            w.WriteUInt16(0);   
            w.WriteZeros(47);   
        });

        
    }
}
