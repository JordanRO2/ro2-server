using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1629 ReqPCollectionSetActivePet - Set active companion pet</summary>
public class PCollectionSetActivePetHandler : ZoneRmiHandlerBase<PCollectionSetActivePetHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqPCollectionSetActivePet;
    public override string Name => "PCollectionSetActivePet";
    public PCollectionSetActivePetHandler(IServiceScopeFactory s, ILogger<PCollectionSetActivePetHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        uint petCollectionId = 0;
        if (payload.Length >= 4)
        {
            var reader = new PacketReader(payload.Span);
            petCollectionId = reader.ReadUInt32();
        }
        Logger.LogInformation("{Name} sets active pet {PetId}", player.Name, petCollectionId);

        
        var active = await PetCollectionUseCaseFactory.From(services, Logger).SetActiveAsync(player.CharacterId, petCollectionId, ct);
        if (!active.Accepted)
            Logger.LogWarning("Collection: char {Char} asked to activate pet {Pet}, which is not in its pet book; " +
                              "active pet stays {Active}", player.CharacterId, petCollectionId, active.PetId);

        
        
        
        
        
        
        
        
        var body = CollectionPackets.BuildAnsPCollectionSetActivePet(active.PetId, active.Flag, 0);
        await session.SendRmiAsync(ZoneS2COpcodes.AnsPCollectionSetActivePet, writer => writer.WriteBytes(body));
    }
}
