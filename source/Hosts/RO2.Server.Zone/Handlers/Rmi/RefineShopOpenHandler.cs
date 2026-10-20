using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Domain.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles RMI 1673 (ReqRefineShopOpen): opens the refine shop.</summary>
public class RefineShopOpenHandler : ZoneRmiHandlerBase<RefineShopOpenHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqRefineShopOpen;
    public override string Name => "RefineShopOpen";

    public RefineShopOpenHandler(IServiceScopeFactory s, ILogger<RefineShopOpenHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        
        
        var nfyRefineBody = ShopItemPackets.BuildNfyBeginRefineShop3003(player.EntityId);
        await Broadcast.BroadcastToNearbyAsync(player, ZoneS2COpcodes.NfyBeginRefineShop,
            writer => writer.WriteBytes(nfyRefineBody));
    }
}
