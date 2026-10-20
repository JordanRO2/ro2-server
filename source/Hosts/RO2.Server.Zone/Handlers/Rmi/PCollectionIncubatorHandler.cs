using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.GameData;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles placing an incubator item into an empty incubator slot (RMI 1630, ReqPCollectionIncubator).</summary>
/// <remarks>
/// <para>
/// Request: the incubator's bag cell, its item id and the target slot index.
/// </para>
/// <para>
/// The cell must hold that incubator and the slot must be empty. On success one incubator leaves the stack, the slot is stored,
/// and the incubator list (2926) is sent with result 0. A refusal changes nothing and still answers the current list.
/// </para>
/// </remarks>
public class PCollectionIncubatorHandler : ZoneRmiHandlerBase<PCollectionIncubatorHandler>
{
    private const int BodyLength = 8;

    public override ushort Opcode => ZoneC2SOpcodes.ReqPCollectionIncubator;
    public override string Name => "PCollectionIncubator";
    public PCollectionIncubatorHandler(IServiceScopeFactory s, ILogger<PCollectionIncubatorHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < BodyLength) return;
        var body = payload.Span;
        byte bag = body[0], cell = body[1], target = body[7];
        uint incubatorId = BinaryPrimitives.ReadUInt32LittleEndian(body[3..]);

        var data = services.GetRequiredService<GameDataStore>();
        var inventory = services.GetRequiredService<IInventoryRepository>();
        var incubators = services.GetRequiredService<IPetIncubatorRepository>();

        using var serialised = await PetIncubatorStore.LockAsync(player.CharacterId, ct);
        var slots = await PetIncubatorStore.LoadAsync(incubators, player.CharacterId, Logger, ct);
        if (slots == null) return;   

        var incubator = PetIncubatorStore.Incubator(data, incubatorId);
        var item = incubator == null ? null
            : await PetIncubatorStore.BagItemAsync(inventory, player.CharacterId, bag, cell, incubatorId);
        var now = DateTime.UtcNow;
        if (incubator == null || item == null
            || !await PetIncubatorStore.TryPlaceAsync(incubators, player.CharacterId, target, incubator.Incubator_ID,
                incubator.Charge_Quantity, now, Logger, ct))
        {
            
            Logger.LogInformation("{Name}: 1630 incubator {Item} from bag {Bag} slot {Cell} into slot {Target} refused (no such incubator, cell does not hold it, or slot not free)",
                player.Name, incubatorId, bag, cell, target);
            await PetIncubatorStore.SendListAsync(session, slots, ct);
            return;
        }

        await PetIncubatorStore.ConsumeOneAsync(session, player, inventory, item, bag, cell, ct);
        Logger.LogInformation("{Name} placed incubator {Item} ({Charges} charges) in slot {Target}",
            player.Name, incubatorId, incubator.Charge_Quantity, target);
        await PetIncubatorStore.SendListAsync(session,
            await PetIncubatorStore.LoadAsync(incubators, player.CharacterId, Logger, ct), ct);
    }
}
