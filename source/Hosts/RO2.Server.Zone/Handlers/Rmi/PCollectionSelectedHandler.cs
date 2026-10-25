using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Inventory.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1625 ReqPCollectionSelected - Select pet collection skill</summary>
public class PCollectionSelectedHandler : ZoneRmiHandlerBase<PCollectionSelectedHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqPCollectionSelected;
    public override string Name => "PCollectionSelected";
    public PCollectionSelectedHandler(IServiceScopeFactory s, ILogger<PCollectionSelectedHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        uint petCollectionId = 0;
        if (payload.Length >= 4)
        {
            var reader = new PacketReader(payload.Span);
            petCollectionId = reader.ReadUInt32();
        }
        Logger.LogDebug("{Name} selects pet collection skill {Id}", player.Name, petCollectionId);

        
        var svc = CollectionService.From(services, Logger);
        var book = await svc.SelectPetSkillAsync(player.CharacterId, petCollectionId, ct);

        
        
        
        
        
        
        var store = services.GetRequiredService<GameDataStore>();
        var (state, pieces) = await MonsterCollectionRules.PetRecordAsync(
            store, services.GetRequiredService<IInventoryRepository>(), player.CharacterId, petCollectionId,
            book.LoyaltyOf(petCollectionId));
        var ans = CollectionPackets.BuildAnsPCollectionSelectedSkill(state, pieces, 0);
        await session.SendRmiAsync(ZoneS2COpcodes.AnsPCollectionSelectedSkill, writer => writer.WriteBytes(ans));

        
        
    }
}
