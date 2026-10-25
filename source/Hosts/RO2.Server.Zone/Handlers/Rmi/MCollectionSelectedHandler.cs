using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Inventory.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles RMI 1621 (ReqMCollectionSelected): selects a monster collection skill.</summary>
public class MCollectionSelectedHandler : ZoneRmiHandlerBase<MCollectionSelectedHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqMCollectionSelected;
    public override string Name => "MCollectionSelected";
    public MCollectionSelectedHandler(IServiceScopeFactory s, ILogger<MCollectionSelectedHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        uint collectionId = 0;
        if (payload.Length >= 4)
        {
            var reader = new PacketReader(payload.Span);
            collectionId = reader.ReadUInt32();
        }
        Logger.LogInformation("{Name} selects monster collection skill {Id}", player.Name, collectionId);

        
        
        var svc = CollectionService.From(services, Logger);
        bool registered = await svc.SelectMonsterSkillAsync(player.CharacterId, collectionId, ct);

        
        
        
        
        var store = services.GetRequiredService<GameDataStore>();
        var progress = await MonsterCollectionProgressStore.From(services, Logger).GetAsync(player.CharacterId, ct);
        var state = MonsterCollectionRules.State(store, progress, collectionId, registered);
        var pieces = await MonsterCollectionRules.MonsterPieceCountsAsync(
            store, services.GetRequiredService<IInventoryRepository>(), player.CharacterId, collectionId);

        
        
        
        
        
        var ans = CollectionPackets.BuildAnsMCollectionSelectedSkill(state, pieces, 0);
        await session.SendRmiAsync(ZoneS2COpcodes.AnsMCollectionSelectedSkill, writer => writer.WriteBytes(ans));
    }
}
