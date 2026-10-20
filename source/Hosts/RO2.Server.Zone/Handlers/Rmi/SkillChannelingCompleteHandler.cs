using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles RMI 1609 ReqSkillChannelingComplete ([u32 skillId]). Completes a channel that ran its full duration.</summary>
/// <remarks>
/// The client sends it when the channeled skill finishes. The reply is 2897 OnSkillChannelingCompleteACK to the caster,
/// with result 0 if that skill is still the channel in progress and 1 if not. Only on result 0 is
/// 2586 OnSkillChannelingCompleteNotify sent to the map, with the caster among the receivers.
/// <para>
/// Result 1 also follows a server-side cancel. A move by the caster during a channel whose skill is not movable ends
/// the channel and sends 2585 to the map; the 1609 that follows then receives result 1 and no 2586.
/// </para>
/// </remarks>
public class SkillChannelingCompleteHandler : ZoneRmiHandlerBase<SkillChannelingCompleteHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqSkillChannelingComplete;
    public override string Name => "SkillChannelingComplete";

    public SkillChannelingCompleteHandler(IServiceScopeFactory s, ILogger<SkillChannelingCompleteHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        
        if (payload.Length < 4)
        {
            Logger.LogWarning("{Name}: ReqSkillChannelingComplete of {Len} bytes, expected 4", player.Name, payload.Length);
            return;
        }
        uint skillId = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(payload.Span);

        bool completed = player.TryEndChannel(skillId);
        ushort result = completed ? SkillChannelingPackets.ChannelCompleted : SkillChannelingPackets.ChannelNotActive;
        await session.SendInternalRmiAsync(ZoneS2COpcodes.OnSkillChannelingCompleteACK,
            SkillChannelingPackets.BuildSkillChannelingCompleteAck2897(skillId, result));

        if (!completed)
        {
            
            Logger.LogDebug("{Name} completes channel {SkillId}, which is not in progress (channel {Active}); ended",
                player.Name, skillId, player.ActiveChannelSkillId);
            return;
        }

        Logger.LogDebug("{Name} completes channeling skill {SkillId}", player.Name, skillId);

        
        
        var body = ActorStatePackets.BuildOnSkillChannelingCompleteNotify(player.EntityId, skillId);
        await Broadcast.BroadcastToMapAsync(player.MapId, ZoneS2COpcodes.OnSkillChannelingCompleteNotify,
            writer => writer.WriteBytes(body));
    }
}
