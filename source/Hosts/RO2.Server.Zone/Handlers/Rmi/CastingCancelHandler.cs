using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1363 ReqCastingCancel07 (ByteArray) - Cancel old casting system</summary>
public class CastingCancelHandler : ZoneRmiHandlerBase<CastingCancelHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqCastingCancel07;
    public override string Name => "CastingCancel";

    public CastingCancelHandler(IServiceScopeFactory s, ILogger<CastingCancelHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        Logger.LogDebug("{Name} cancels casting", player.Name);

        
        await session.SendRmiAsync(ZoneS2COpcodes.OnCancelSkill, writer =>
        {
            writer.WriteUInt32(player.EntityId);
            writer.WriteUInt16(0); 
        });

        
        await Broadcast.BroadcastToNearbyAsync(player, ZoneS2COpcodes.OnCancelSkillNotify, writer =>
        {
            writer.WriteUInt32(player.EntityId);
        });
    }
}
