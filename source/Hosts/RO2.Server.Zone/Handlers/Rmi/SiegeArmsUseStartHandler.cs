using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles RMI 1637 ReqSiegeArmsUseStart. Starts using a siege weapon.</summary>
public class SiegeArmsUseStartHandler : ZoneRmiHandlerBase<SiegeArmsUseStartHandler>
{
 /// <summary>
 /// Refusal result 24311 for a siege arm that another player is operating. The client ignores nonzero results.
 /// The server never sends refusal result 24310, since it does not track arm settings.
 /// </summary>
    internal const ushort ResultArmOccupied = 24311;

    public override ushort Opcode => ZoneC2SOpcodes.ReqSiegeArmsUseStart;
    public override string Name => "SiegeArmsUseStart";
    public SiegeArmsUseStartHandler(IServiceScopeFactory s, ILogger<SiegeArmsUseStartHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        uint siegeArmsHandle = payload.Length >= 4 ? new PacketReader(payload.Span).ReadUInt32() : 0u;

        
        
        
        var holder = LiveOperatorOf(siegeArmsHandle, player);
        if (holder is not null)
        {
            Logger.LogInformation("{Name} cannot use siege weapon {Handle}: {Holder} is operating it",
                player.Name, siegeArmsHandle, holder.Name);
            await session.SendRmiAsync(ZoneS2COpcodes.AnsSiegeArmsUseStart,
                writer => writer.WriteBytes(BuildSeatRefusal2937(ResultArmOccupied)));
            return;
        }

        Logger.LogInformation("{Name} starts using siege weapon (handle {Handle})", player.Name, siegeArmsHandle);

        
        
        
        
        
        
        
        
        
        
        var cooldowns = SiegeService.ActiveCooldowns(siegeArmsHandle, DateTime.UtcNow);
        await session.SendRmiAsync(ZoneS2COpcodes.AnsSiegeArmsUseStart,
            writer => writer.WriteBytes(SiegePackets.BuildSiegeArmsUseStart2937(siegeArmsHandle, cooldowns)));

        
        
        var arm = SiegeService.BeginOperate(session.SessionId, siegeArmsHandle, player.EntityId);

        
        
        
        
        
        await session.SendRmiAsync(ZoneS2COpcodes.NfySiegeArmsUserChanged,
            w => w.WriteBytes(SiegePackets.BuildSiegeArmsUserChanged(arm.Handle, arm.OperatorHandle)));
        await Broadcast.BroadcastToNearbyAsync(player, ZoneS2COpcodes.NfySiegeArmsUserChanged,
            w => w.WriteBytes(SiegePackets.BuildSiegeArmsUserChanged(arm.Handle, arm.OperatorHandle)));

        
        
        
        
        
    }

 /// <summary>
 /// Returns the player operating <paramref name="armHandle"/>, or null when the arm is free or the requester already holds it.
 /// </summary>
 /// <remarks>
 /// The recorded operator counts only while that player is still in the world and its session still maps to this arm.
 /// Otherwise the record is stale and the arm is treated as free.
 /// </remarks>
    private PlayerState? LiveOperatorOf(uint armHandle, PlayerState requester)
    {
        if (!SiegeService.TryGet(armHandle, out var arm)) return null;
        uint operatorHandle = arm.OperatorHandle;
        if (operatorHandle == 0 || operatorHandle == requester.EntityId) return null;

        var holder = PlayerManager.GetAllPlayers().FirstOrDefault(p => p.EntityId == operatorHandle);
        if (holder is null) return null;
        return SiegeService.GetOperatedArm(holder.SessionId)?.Handle == armHandle ? holder : null;
    }

 /// <summary>
 /// Builds the refusal reply (2937): a nonzero result, a zero arm handle and an empty cooldown list.
 /// </summary>
    internal static byte[] BuildSeatRefusal2937(ushort result)
    {
        using var w = new PacketWriter(12);
        w.WriteUInt32(8);      
        w.WriteUInt16(result); 
        w.WriteUInt32(0);      
        w.WriteUInt16(0);      
        return w.ToArray();
    }
}
