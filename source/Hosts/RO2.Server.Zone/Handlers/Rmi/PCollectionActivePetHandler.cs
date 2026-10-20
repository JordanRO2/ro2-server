using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1628 ReqPCollectionActivePet - Get active pet info</summary>
public class PCollectionActivePetHandler : ZoneRmiHandlerBase<PCollectionActivePetHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqPCollectionActivePet;
    public override string Name => "PCollectionActivePet";
    public PCollectionActivePetHandler(IServiceScopeFactory s, ILogger<PCollectionActivePetHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        
        
        
        
        
        
        
        
        
        
        var svc = CollectionService.From(services, Logger);
        var (petId, level) = await svc.GetActivePetAsync(player.CharacterId, ct);
        Logger.LogDebug("{Name} queries active pet -> {Pet} (loyalty level {Level})", player.Name, petId, level);

        byte[] body = petId != 0
            ? CollectionPackets.BuildAnsPCollectionSetActivePet(petId, level, 0)
            : CollectionPackets.BuildAnsPCollectionSetActivePet(0, 0, 30001);
        await session.SendRmiAsync(ZoneS2COpcodes.AnsPCollectionSetActivePet, writer => writer.WriteBytes(body));
    }
}
