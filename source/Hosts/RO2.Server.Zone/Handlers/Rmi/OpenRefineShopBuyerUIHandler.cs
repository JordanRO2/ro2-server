using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Domain.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1672 ReqOpenRefineShopBuyerUI (ByteArray) - Open refine shop buyer UI</summary>
public class OpenRefineShopBuyerUIHandler : ZoneRmiHandlerBase<OpenRefineShopBuyerUIHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqOpenRefineShopBuyerUI;
    public override string Name => "OpenRefineShopBuyerUI";

    public OpenRefineShopBuyerUIHandler(IServiceScopeFactory s, ILogger<OpenRefineShopBuyerUIHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        Logger.LogDebug("{Name} opens refine shop buyer UI", player.Name);
        return Task.CompletedTask;
    }
}
