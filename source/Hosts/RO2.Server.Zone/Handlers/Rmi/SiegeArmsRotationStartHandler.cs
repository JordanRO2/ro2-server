using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles RMI 1639 ReqSiegeArmsRotationStart. Starts rotating a siege weapon.</summary>
public class SiegeArmsRotationStartHandler : ZoneRmiHandlerBase<SiegeArmsRotationStartHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqSiegeArmsRotationStart;
    public override string Name => "SiegeArmsRotationStart";
    public SiegeArmsRotationStartHandler(IServiceScopeFactory s, ILogger<SiegeArmsRotationStartHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        byte direction = payload.Length >= 1 ? payload.Span[0] : (byte)0;
        Logger.LogDebug("{Name} starts siege rotation (dir {Dir})", player.Name, direction);

        
        
        
        var arm = SiegeService.GetOperatedArm(session.SessionId);
        if (arm == null)
        {
            Logger.LogDebug("{Name} sent 1639 without an operated siege arm; no 2941", player.Name);
            return;
        }

        
        
        
        
        
        await session.SendRmiAsync(ZoneS2COpcodes.NfySiegeArmsRotationStart,
            writer => writer.WriteBytes(EconomyMiscPackets.BuildSiegeArmsRotationStart(arm.Handle, direction)));
        await Broadcast.BroadcastToNearbyAsync(player, ZoneS2COpcodes.NfySiegeArmsRotationStart,
            w => w.WriteBytes(EconomyMiscPackets.BuildSiegeArmsRotationStart(arm.Handle, direction)));
    }
}
