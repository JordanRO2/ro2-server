using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles RMI 1320 (ReqRidingQuickSlot). The request is accepted without a reply.</summary>
public class RidingQuickSlotHandler : ZoneRmiHandlerBase<RidingQuickSlotHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqRidingQuickSlot;
    public override string Name => "RidingQuickSlot";
    public RidingQuickSlotHandler(IServiceScopeFactory s, ILogger<RidingQuickSlotHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < 8) return;
        Logger.LogDebug("{Name} sets riding quick slot (no reply sent -- see below)", player.Name);

        
        
        
        
        
        
        
    }
}
