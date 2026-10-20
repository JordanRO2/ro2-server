using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1451 ReqCraftSaleBuyerOpenUI: opens the craft sale buyer window.</summary>
public class CraftSaleBuyerOpenUIHandler : ZoneRmiHandlerBase<CraftSaleBuyerOpenUIHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqCraftSaleBuyerOpenUI;
    public override string Name => "CraftSaleBuyerOpenUI";
    public CraftSaleBuyerOpenUIHandler(IServiceScopeFactory s, ILogger<CraftSaleBuyerOpenUIHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        Logger.LogDebug("{Name} opens craft sale buyer UI", player.Name);
        
        await session.SendRmiAsync(ZoneS2COpcodes.OnAckCraftSaleBuyerOpenUI, writer =>
        {
            var pos = writer.Reserve(4);
            var start = writer.Length;
            writer.WriteUInt16(0); 
            writer.WriteAt(pos, (uint)(writer.Length - start));
        });
    }
}
