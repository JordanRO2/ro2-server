using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.GameData;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles RMI 1620 (ReqMCollection): opens the monster collection book.</summary>
public class MCollectionHandler : ZoneRmiHandlerBase<MCollectionHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqMCollection;
    public override string Name => "MCollection";
    public MCollectionHandler(IServiceScopeFactory s, ILogger<MCollectionHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        
        
        
        
        var svc = CollectionService.From(services, Logger);
        var book = await svc.GetStateAsync(player.CharacterId, ct);
        var store = services.GetRequiredService<GameDataStore>();
        var progress = await MonsterCollectionProgressStore.From(services, Logger).GetAsync(player.CharacterId, ct);
        var records = MonsterCollectionRules.BookStates(store, book.RegisteredMonsterIds, progress);
        Logger.LogDebug("{Name} opens monster collection ({N} registered)", player.Name, records.Count);

        var body = CollectionPackets.BuildAnsMCollection(records, MonsterCollectionRules.RateFor(store, records.Count));
        await session.SendRmiAsync(ZoneS2COpcodes.AnsMCollection, writer => writer.WriteBytes(body));
    }
}
