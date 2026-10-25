using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1624 ReqPCollection - Open pet collection book</summary>
public class PCollectionHandler : ZoneRmiHandlerBase<PCollectionHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqPCollection;
    public override string Name => "PCollection";
    public PCollectionHandler(IServiceScopeFactory s, ILogger<PCollectionHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        
        
        
        
        
        var svc = CollectionService.From(services, Logger);
        var state = await svc.GetStateAsync(player.CharacterId, ct);
        var records = PetCollectionBook.BookStates(services.GetRequiredService<GameDataStore>(), state);
        Logger.LogDebug("{Name} opens pet collection ({N} registered)", player.Name, records.Count);

        var body = CollectionPackets.BuildAnsPCollection(records);
        await session.SendRmiAsync(ZoneS2COpcodes.AnsPCollection, writer => writer.WriteBytes(body));

        
        
        
        
        
        
        
        
        
        
        
        var incubators = services.GetRequiredService<IPetIncubatorRepository>();
        IReadOnlyList<RO2.Domain.Entities.PetIncubatorSlot>? slots;
        using (await PetIncubatorStore.LockAsync(player.CharacterId, ct))
            slots = await PetIncubatorStore.LoadAsync(incubators, player.CharacterId, Logger, ct);
        await PetIncubatorStore.SendListAsync(session, slots, ct);
    }
}
