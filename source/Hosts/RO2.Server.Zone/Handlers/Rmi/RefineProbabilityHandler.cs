using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles RMI 1669 (ReqRefineProbability): returns the refine success probability.</summary>
public class RefineProbabilityHandler : ZoneRmiHandlerBase<RefineProbabilityHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqRefineProbability;
    public override string Name => "RefineProbability";

    public RefineProbabilityHandler(IServiceScopeFactory s, ILogger<RefineProbabilityHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        
        
        
        
        
        
        await session.SendRmiAsync(ZoneS2COpcodes.OnRefineProability,
            w => w.WriteBytes(ShopItemPackets.BuildRefineProbability2996(player.EntityId)));
    }
}
