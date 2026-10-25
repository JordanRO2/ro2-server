using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles RMI 1553 ReqAbilValue, which requests the player's ability values.</summary>
public class AbilValueHandler : ZoneRmiHandlerBase<AbilValueHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqAbilValue;
    public override string Name => "AbilValue";

    public AbilValueHandler(IServiceScopeFactory s, ILogger<AbilValueHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        await session.SendRmiAsync(ZoneS2COpcodes.AnsAbilValue, writer =>
        {
            writer.WriteUInt32(player.EntityId);
            writer.WriteUInt16(0); 
        });
    }
}
