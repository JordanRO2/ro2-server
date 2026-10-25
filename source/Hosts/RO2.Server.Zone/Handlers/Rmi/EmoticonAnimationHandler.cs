using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles RMI 1370 ReqEmoticonAnimation, which plays an emoticon animation for the player and nearby players.</summary>
public class EmoticonAnimationHandler : ZoneRmiHandlerBase<EmoticonAnimationHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqEmoticonAnimation;
    public override string Name => "EmoticonAnimation";

    public EmoticonAnimationHandler(IServiceScopeFactory s, ILogger<EmoticonAnimationHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        
        
        
        if (payload.Length < 14) return;
        var reader = new PacketReader(payload.Span);
        reader.Skip(10);
        var emoticonId = reader.ReadUInt32();

        Logger.LogDebug("{Name} plays emoticon {EmoticonId}", player.Name, emoticonId);

        
        
        
        
        
        var body = ActorStatePackets.BuildOnEmoticonAni(player.EntityId, emoticonId);

        
        
        
        
        
        
        await session.SendInternalRmiAsync(ZoneS2COpcodes.OnEmoticonAni, body);

        await Broadcast.BroadcastToNearbyAsync(player, ZoneS2COpcodes.OnEmoticonAni,
            writer => writer.WriteBytes(body));
    }
}
