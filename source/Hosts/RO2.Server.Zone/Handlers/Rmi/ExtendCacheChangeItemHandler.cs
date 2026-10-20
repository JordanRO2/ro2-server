using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.GameData;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles RMI 1584 ReqExtendCacheChangeItem, which uses an item-change key item on its target item.</summary>
/// <remarks>
/// The request body holds two bag and slot pairs, each with an item instance id. The exchange consumes one of each
/// item and grants one entry from the key item's random set. The reply is a single AnsExtendCacheChangeItem (2851)
/// carrying the outcome and the gained item.
/// </remarks>
public class ExtendCacheChangeItemHandler : ZoneRmiHandlerBase<ExtendCacheChangeItemHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqExtendCacheChangeItem;
    public override string Name => "ExtendCacheChangeItem";

    public ExtendCacheChangeItemHandler(IServiceScopeFactory s, ILogger<ExtendCacheChangeItemHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (!ItemChangeExchange.TryParseRequest(payload.Span, out var req))
        {
            Logger.LogWarning("Session {Id}: ExtendCacheChangeItem body is {Len} bytes, not the {Need} retail sends",
                session.SessionId, payload.Length, ItemChangeExchange.RequestLength);
            return;
        }

        var inventory = services.GetRequiredService<IInventoryRepository>();
        var store = services.GetRequiredService<GameDataStore>();
        ItemChangeExchange.Outcome outcome;
        try
        {
            outcome = await ItemChangeExchange.ExchangeAsync(inventory, store, player.CharacterId, req.UidA, req.UidB,
                max => Random.Shared.NextInt64(max));
        }
        catch (Exception ex)
        {
            
            Logger.LogError(ex, "{Name}: item-change {A} on {B} failed while writing the bag", player.Name, req.UidA, req.UidB);
            outcome = new ItemChangeExchange.Outcome(ItemChangeExchange.ResultSystemError,
                ItemChangeExchange.BuildRefusal2851(ItemChangeExchange.ResultSystemError, req.UidA), 0, 0, 0);
        }

        Logger.LogInformation("{Name} item-change {A} (bag {BagA}/slot {SlotA}) on {B} (bag {BagB}/slot {SlotB}), type {Type} -> result {Result}, gained {Count} x {Item}",
            player.Name, req.UidA, req.BagA, req.SlotA, req.UidB, req.BagB, req.SlotB, outcome.ChangeType,
            outcome.Result, outcome.GainedCount, outcome.GainedItemId);

        await session.SendRmiAsync(ZoneS2COpcodes.AnsExtendCacheChangeItem, (ReadOnlyMemory<byte>)outcome.Body);
    }
}
