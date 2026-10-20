using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1369 (ReqSocialAnimation). Plays a social emote for the caster and nearby players.</summary>
public class SocialAnimationHandler : ZoneRmiHandlerBase<SocialAnimationHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqSocialAnimation;
    public override string Name => "SocialAnimation";

    public SocialAnimationHandler(IServiceScopeFactory s, ILogger<SocialAnimationHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        
        
        
        
        
        
        if (payload.Length < 14) return;
        var animId = BinaryPrimitives.ReadUInt32LittleEndian(payload.Span[10..14]);

        Logger.LogDebug("Session {Id}: {Name} emote {AnimId}", session.SessionId, player.Name, animId);

        
        
        
        var body = CombatPackets.BuildSocialAni2396(player.EntityId, animId);

        
        
        
        
        await session.SendInternalRmiAsync(ZoneS2COpcodes.OnSocialAni, body);

        await Broadcast.BroadcastToNearbyAsync(player, ZoneS2COpcodes.OnSocialAni,
            writer => writer.WriteBytes(body));
    }
}
