using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles RMI 1640 ReqSiegeArmsRotationStop. Stops rotating a siege weapon.</summary>
public class SiegeArmsRotationStopHandler : ZoneRmiHandlerBase<SiegeArmsRotationStopHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqSiegeArmsRotationStop;
    public override string Name => "SiegeArmsRotationStop";
    public SiegeArmsRotationStopHandler(IServiceScopeFactory s, ILogger<SiegeArmsRotationStopHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        float yaw = payload.Length >= 4 ? new PacketReader(payload.Span).ReadSingle() : 0f;
        Logger.LogDebug("{Name} stops siege rotation (yaw {Yaw})", player.Name, yaw);

        
        
        
        
        
        var arm = SiegeService.GetOperatedArm(session.SessionId);
        if (arm == null)
        {
            Logger.LogDebug("{Name} sent 1640 without an operated siege arm; no 2942", player.Name);
            return;
        }
        arm.Yaw = yaw;

        
        
        
        
        await session.SendRmiAsync(ZoneS2COpcodes.NfySiegeArmsRotationStop,
            writer => writer.WriteBytes(EconomyMiscPackets.BuildSiegeArmsRotationStop(arm.Handle, yaw)));
        await Broadcast.BroadcastToNearbyAsync(player, ZoneS2COpcodes.NfySiegeArmsRotationStop,
            w => w.WriteBytes(EconomyMiscPackets.BuildSiegeArmsRotationStop(arm.Handle, yaw)));
    }
}
