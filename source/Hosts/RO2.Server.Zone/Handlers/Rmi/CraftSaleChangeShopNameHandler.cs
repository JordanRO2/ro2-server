using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1450 ReqCraftSaleChangeShopName: changes the craft sale shop name.</summary>
public class CraftSaleChangeShopNameHandler : ZoneRmiHandlerBase<CraftSaleChangeShopNameHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqCraftSaleChangeShopName;
    public override string Name => "CraftSaleChangeShopName";
    public CraftSaleChangeShopNameHandler(IServiceScopeFactory s, ILogger<CraftSaleChangeShopNameHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < 42) return;
        Logger.LogInformation("{Name} changes craft sale shop name", player.Name);

        
        
        
        await session.SendRmiAsync(ZoneS2COpcodes.OnAckCraftSaleChangeShopName, w =>
        {
            w.WriteUInt16(0);   
            w.WriteZeros(46);   
        });

        
    }
}
