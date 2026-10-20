using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles RMI 1445 ReqCraftSaleSellerOpenUI: opens the craft sale seller interface.</summary>
public class CraftSaleSellerOpenUIHandler : ZoneRmiHandlerBase<CraftSaleSellerOpenUIHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqCraftSaleSellerOpenUI;
    public override string Name => "CraftSaleSellerOpenUI";
    public CraftSaleSellerOpenUIHandler(IServiceScopeFactory s, ILogger<CraftSaleSellerOpenUIHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        Logger.LogDebug("{Name} opens craft sale seller UI", player.Name);
        
        await session.SendRmiAsync(ZoneS2COpcodes.OnAckCraftSaleSellerOpenUI, writer =>
        {
            var pos = writer.Reserve(4);
            var start = writer.Length;
            writer.WriteUInt16(0); 
            writer.WriteAt(pos, (uint)(writer.Length - start));
        });
    }
}
