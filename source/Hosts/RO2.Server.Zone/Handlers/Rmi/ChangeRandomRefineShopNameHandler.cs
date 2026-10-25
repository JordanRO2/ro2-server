using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1675 ReqChangeRandomRefineShopName: renames the random refine shop.</summary>
public class ChangeRandomRefineShopNameHandler : ZoneRmiHandlerBase<ChangeRandomRefineShopNameHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqChangeRandomRefineShopName;
    public override string Name => "ChangeRandomRefineShopName";

    public ChangeRandomRefineShopNameHandler(IServiceScopeFactory s, ILogger<ChangeRandomRefineShopNameHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        await session.SendRmiAsync(ZoneS2COpcodes.AnsChangeRandomRefineShopName, writer =>
        {
            writer.WriteUInt32(player.EntityId);
            writer.WriteUInt16(0); 
        });
    }
}
