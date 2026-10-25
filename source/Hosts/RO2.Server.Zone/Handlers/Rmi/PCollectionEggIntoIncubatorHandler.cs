using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.GameData;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Inventory.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles placing a pet egg into an incubator slot (RMI 1631, ReqPCollectionEggIntoIncubator).</summary>
/// <remarks>
/// <para>
/// Request: the egg's bag cell, the egg item id, the incubator id and the target slot index.
/// </para>
/// <para>
/// The egg must be in the cell, its pet data must exist, and the target slot must hold that incubator with a charge left and no egg.
/// On success one egg leaves the stack, the egg starts incubating, and the incubator list (2926) is sent with the slot's new state.
/// Otherwise the unchanged list is answered with result 0.
/// </para>
/// </remarks>
public class PCollectionEggIntoIncubatorHandler : ZoneRmiHandlerBase<PCollectionEggIntoIncubatorHandler>
{
    private const int BodyLength = 12;

    public override ushort Opcode => ZoneC2SOpcodes.ReqPCollectionEggIntoIncubator;
    public override string Name => "PCollectionEggIntoIncubator";
    public PCollectionEggIntoIncubatorHandler(IServiceScopeFactory s, ILogger<PCollectionEggIntoIncubatorHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < BodyLength) return;
        var body = payload.Span;
        byte bag = body[0], cell = body[1], target = body[11];
        uint eggId = BinaryPrimitives.ReadUInt32LittleEndian(body[3..]);
        uint incubatorId = BinaryPrimitives.ReadUInt32LittleEndian(body[7..]);

        var data = services.GetRequiredService<GameDataStore>();
        var inventory = services.GetRequiredService<IInventoryRepository>();
        var incubators = services.GetRequiredService<IPetIncubatorRepository>();

        using var serialised = await PetIncubatorStore.LockAsync(player.CharacterId, ct);
        var slots = await PetIncubatorStore.LoadAsync(incubators, player.CharacterId, Logger, ct);
        if (slots == null) return;   

        var pet = PetIncubatorStore.PetByEgg(data, eggId);
        var incubator = PetIncubatorStore.Incubator(data, incubatorId);
        var egg = pet == null || incubator == null ? null
            : await PetIncubatorStore.BagItemAsync(inventory, player.CharacterId, bag, cell, eggId);
        var now = DateTime.UtcNow;
        if (pet == null || incubator == null || egg == null
            || !await PetIncubatorStore.TryStartAsync(incubators, player.CharacterId, target, incubatorId, pet.Pet_Egg_ID,
                now, now.AddSeconds(PetIncubatorStore.IncubationSeconds(incubator, pet)), Logger, ct))
        {
            Logger.LogInformation("{Name}: 1631 egg {Egg} from bag {Bag} slot {Cell} into incubator {Inc} slot {Target} refused (unknown egg or incubator, cell does not hold it, or slot not ready)",
                player.Name, eggId, bag, cell, incubatorId, target);
            await PetIncubatorStore.SendListAsync(session, slots, ct);
            return;
        }

        await PetIncubatorStore.ConsumeOneAsync(session, player, inventory, egg, bag, cell, ct);
        Logger.LogInformation("{Name} put egg {Egg} (pet {Pet}) into incubator {Inc} slot {Target}: hatches in {Secs} s",
            player.Name, eggId, pet.NPC_ID, incubatorId, target, PetIncubatorStore.IncubationSeconds(incubator, pet));
        await PetIncubatorStore.SendListAsync(session,
            await PetIncubatorStore.LoadAsync(incubators, player.CharacterId, Logger, ct), ct);
    }
}
