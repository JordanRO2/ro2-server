using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles RMI 1308 ActorSitDown. Makes the player sit or stand.</summary>
public class SitDownHandler : ZoneRmiHandlerBase<SitDownHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ActorSitDown;
    public override string Name => "SitDown";

    public SitDownHandler(IServiceScopeFactory s, ILogger<SitDownHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        Logger.LogDebug("Session {Id}: {Name} sit/stand", session.SessionId, player.Name);

        
        
        byte state = payload.Length > 10 ? payload.Span[10] : (byte)0;

        
        
        
        
        
        
        byte pose = ActorStateNotifyPackets.StateEnum(state);   

        
        
        
        
        
        
        var stateBody = CombatPackets.BuildChangeState2398(player.EntityId, pose, trailing: 2);

        
        
        
        
        var body = ActorStatePackets.BuildOnSitDown(player.EntityId, state);

        
        
        
        
        
        var notifyBody = ActorStateNotifyPackets.BuildOnSitDownNotify(
            player.EntityId, pose, player.X, player.Y, player.Z);

        await session.SendInternalRmiAsync(ZoneS2COpcodes.OnChangeState, stateBody);
        await session.SendInternalRmiAsync(ZoneS2COpcodes.OnSitDown, body);
        await session.SendInternalRmiAsync(ZoneS2COpcodes.OnSitDownNotify, notifyBody);

        await Broadcast.BroadcastToNearbyAsync(player, ZoneS2COpcodes.OnChangeState,
            writer => writer.WriteBytes(stateBody));
        await Broadcast.BroadcastToNearbyAsync(player, ZoneS2COpcodes.OnSitDownNotify,
            writer => writer.WriteBytes(notifyBody));
    }
}
