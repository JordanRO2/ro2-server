using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles RMI 1440 ReqSkillChannelingCancel. Cancels the channeled skill.</summary>
/// <remarks>
/// The request body is a single u32 skill id ([u32 skillId]), the same shape as the 1507 and 1609 requests.
/// </remarks>
public class SkillChannelingCancelHandler : ZoneRmiHandlerBase<SkillChannelingCancelHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqSkillChannelingCancel;
    public override string Name => "SkillChannelingCancel";

    public SkillChannelingCancelHandler(IServiceScopeFactory s, ILogger<SkillChannelingCancelHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        
        if (payload.Length < 4)
        {
            Logger.LogWarning("{Name}: ReqSkillChannelingCancel of {Len} bytes, expected 4", player.Name, payload.Length);
            return;
        }
        uint skillId = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(payload.Span);

        
        
        bool wasActive = player.TryEndChannel(skillId);
        Logger.LogDebug("{Name} cancels channeling skill {SkillId}{State}", player.Name, skillId,
            wasActive ? "" : " (not the channel in progress)");

        
        
        
        
        
        
        var body = CombatSkillNotifyPackets.BuildSkillChannelingCancelNotify2585(player.EntityId, skillId);
        await Broadcast.BroadcastToNearbyAsync(player, ZoneS2COpcodes.OnSkillChannelingCancelNotify,
            writer => writer.WriteBytes(body));
    }
}
