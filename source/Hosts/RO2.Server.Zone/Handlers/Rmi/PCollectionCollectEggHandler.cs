using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles the collection of a hatched egg from an incubator (RMI 1632, ReqPCollectionCollectEgg).</summary>
/// <remarks>
/// <para>
/// Request: <c>[u32 incubator id][u8 slot index]</c>.
/// </para>
/// <para>
/// The egg is collected only when the slot holds that incubator and a hatched egg, the egg's pet data exists, and the bag
/// can take the hatched egg item. On success the slot loses the egg and one charge, the hatched egg is added to the bag,
/// and the incubator list (2926) is sent. Otherwise the unchanged list is answered with result 0.
/// </para>
/// </remarks>
public class PCollectionCollectEggHandler : ZoneRmiHandlerBase<PCollectionCollectEggHandler>
{
    private const int BodyLength = 5;

    public override ushort Opcode => ZoneC2SOpcodes.ReqPCollectionCollectEgg;
    public override string Name => "PCollectionCollectEgg";
    public PCollectionCollectEggHandler(IServiceScopeFactory s, ILogger<PCollectionCollectEggHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < BodyLength) return;
        uint incubatorId = BinaryPrimitives.ReadUInt32LittleEndian(payload.Span);
        byte target = payload.Span[4];

        var data = services.GetRequiredService<GameDataStore>();
        var inventory = services.GetRequiredService<IInventoryRepository>();
        var incubators = services.GetRequiredService<IPetIncubatorRepository>();

        using var serialised = await PetIncubatorStore.LockAsync(player.CharacterId, ct);
        var slots = await PetIncubatorStore.LoadAsync(incubators, player.CharacterId, Logger, ct);
        if (slots == null) return;   

        var now = DateTime.UtcNow;
        var slot = PetIncubatorRules.CollectableSlot(slots, target, incubatorId, now);
        var pet = slot == null ? null : PetIncubatorStore.PetByEgg(data, slot.EggId);
        var landing = pet == null ? null
            : ItemPickUpHandler.PlanLanding(await inventory.GetByCharacterAsync(player.CharacterId),
                player.CharacterId, pet.Hatched_Egg_ID, 1, data.Items);
        if (slot == null || pet == null || landing == null || landing.Refused)
        {
            Logger.LogInformation("{Name}: 1632 collect from incubator {Inc} slot {Target} refused (no such slot, no hatched egg, or bag full)",
                player.Name, incubatorId, target);
            await PetIncubatorStore.SendListAsync(session, slots, ct);
            return;
        }

        var before = await PetIncubatorStore.TryCollectAsync(incubators, player.CharacterId, target, incubatorId, now, Logger, ct);
        if (before == null)
        {
            
            await PetIncubatorStore.SendListAsync(session, slots, ct);
            return;
        }

        var landed = await ItemPickUpHandler.WriteLandingAsync(inventory, landing, player, pet.Hatched_Egg_ID, Logger);
        if (landed.Count == 0)
        {
            
            await PetIncubatorStore.RestoreAsync(incubators, player.CharacterId, before, Logger, ct);
            await PetIncubatorStore.SendListAsync(session, slots, ct);
            return;
        }

        
        
        await services.GetRequiredService<LootService>().AdvanceCollectQuestsAsync(session, player, pet.Hatched_Egg_ID, 1);
        await KharaService.OnHeldItemChangedAsync(session, player, pet.Hatched_Egg_ID, services, Logger);
        foreach (var stack in landed)
            await session.SendRmiAsync(ZoneS2COpcodes.OnPickupItem,
                LootNotifyPackets.BuildPickupItem2333(WorldEntryService.BuildItemRecord126(stack)), ct);

        Logger.LogInformation("{Name} collected hatched egg {Hatched} (pet {Pet}) from incubator {Inc} slot {Target}; {Charges} charge(s) left",
            player.Name, pet.Hatched_Egg_ID, pet.NPC_ID, incubatorId, target, before.Charges - 1);
        await PetIncubatorStore.SendListAsync(session,
            await PetIncubatorStore.LoadAsync(incubators, player.CharacterId, Logger, ct), ct);
    }
}
