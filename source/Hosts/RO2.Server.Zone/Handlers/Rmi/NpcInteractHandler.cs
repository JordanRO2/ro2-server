using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Domain.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// Handles the start of a function-object or NPC control (RMI 1410, ReqFunctionObjectControl).
/// </summary>
/// <remarks>
/// The controller receives its pose for the kinds that have one, followed by the control bar and its broadcast.
/// The control is recorded so that the cancel (1411) and reward (1412) handlers can end it.
/// </remarks>
public class NpcInteractHandler : ZoneRmiHandlerBase<NpcInteractHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqFunctionObjectControl;
    public override string Name => "NpcInteract";

    public NpcInteractHandler(IServiceScopeFactory s, ILogger<NpcInteractHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        
        
        
        
        
        if (payload.Length < 14) return;
        var reader = new PacketReader(payload.Span);
        reader.Skip(10);
        
        
        
        
        var funcObjectHandle = reader.ReadUInt32();

        
        
        
        
        
        
        var npcState = services.GetRequiredService<NpcStateManager>().GetNpc(funcObjectHandle);
        Logger.LogDebug("Session {Id}: {Name} controls func-object {Handle:X8} (npc={NpcId})",
            session.SessionId, player.Name, funcObjectHandle, npcState?.Spawn?.AssetIndex ?? 0);

        
        
        
        
        
        
        
        
        var controlState = FuncObjectControlPackets.ControlStateFor(
            services.GetRequiredService<GameDataStore>(), player.MapId, funcObjectHandle);
        FuncObjectControlState.Begin(player, new FuncObjectControl(funcObjectHandle, controlState));
        if (controlState is { } state)
        {
            var stateBody = FuncObjectControlPackets.BuildControlState2398(player.EntityId, state);
            await session.SendRmiAsync(ZoneS2COpcodes.OnChangeState, writer => writer.WriteBytes(stateBody));
            await Broadcast.BroadcastToNearbyAsync(player, ZoneS2COpcodes.OnChangeState, writer => writer.WriteBytes(stateBody));
        }

        
        
        
        
        
        
        await session.SendRmiAsync(ZoneS2COpcodes.OnFuncObjectControl,
            writer => writer.WriteBytes(NpcDialoguePackets.BuildFuncObjectControl2515(funcObjectHandle)));

        
        
        
        
        
        
        
        var notify2516 = NpcDialoguePackets.BuildFuncObjectControlNotify2516(player.EntityId, funcObjectHandle);
        await session.SendRmiAsync(ZoneS2COpcodes.OnFuncObjectControlNotify, writer => writer.WriteBytes(notify2516));

        await Broadcast.BroadcastToNearbyAsync(player, ZoneS2COpcodes.OnFuncObjectControlNotify,
            writer => writer.WriteBytes(notify2516));

        
        
        
        
        
        
        
        
    }
}
