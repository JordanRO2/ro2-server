using RO2.Economy.Domain;
using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// Handles RMI 1472 (ReqWebItemAtMyInven): moves units of a web mall box entry into the bag.
/// </summary>
/// <remarks>
/// Request: fixed 5 bytes, <c>[u32 entryId][u8 count]</c>. The entry ids are the ones listed by
/// <see cref="WebMallItemSearchHandler"/>.
/// Each delivered unit is answered with one 2641 AnsWebItemAtMyInven. Every unit lands in its own bag
/// stack, as account gift claims do (<see cref="ItemPickUpHandler.PlanLanding"/>).
/// Units that do not fit in the bag stay in the box. An unknown entry or a full bag gets no reply.
/// </remarks>
public class WebItemAtMyInvenHandler : ZoneRmiHandlerBase<WebItemAtMyInvenHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqWebItemAtMyInven;
    public override string Name => "WebItemAtMyInven";
    public WebItemAtMyInvenHandler(IServiceScopeFactory s, ILogger<WebItemAtMyInvenHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        var span = payload.Span;
        if (span.Length < 5)
        {
            Logger.LogWarning("Session {Id}: WebItemAtMyInven body is {Len} bytes, not retail's 5",
                session.SessionId, span.Length);
            return;
        }
        uint entryId = BinaryPrimitives.ReadUInt32LittleEndian(span);
        byte asked = span[4];
        if (asked == 0 || player.AccountId == 0) return;

        var store = services.GetRequiredService<ICashMallRepository>();
        CashMallBoxEntry? entry;
        try
        {
            entry = await store.GetBoxEntryAsync(player.AccountId, entryId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "{Name}: web mall box of account {Account} could not be read; entry {Entry} not delivered",
                player.Name, player.AccountId, entryId);
            return;
        }
        if (entry is null)
        {
            Logger.LogDebug("{Name}: 1472 for entry {Entry}, which is not in account {Account}'s web mall box: nothing delivered",
                player.Name, entryId, player.AccountId);
            return;
        }

        int units = Math.Min(asked, entry.Count);
        
        var taken = await store.TryTakeUnitsAsync(player.AccountId, entryId, units, ct);
        if (taken is null) return; 

        var data = services.GetRequiredService<GameDataStore>();
        var inventory = services.GetRequiredService<IInventoryRepository>();
        var records = new List<byte[]>(units);
        try
        {
            var owned = (await inventory.GetByCharacterAsync(player.CharacterId)).ToList();
            for (int i = 0; i < units; i++)
            {
                var landing = ItemPickUpHandler.PlanLanding(owned, player.CharacterId, taken.ItemId, 1, data.Items);
                if (landing.Refused) break;
                var landed = await ItemPickUpHandler.WriteLandingAsync(inventory, landing, player, taken.ItemId, Logger);
                if (landed.Count == 0) break;
                foreach (var stack in landing.NewStacks)
                    if (landed.Contains(stack)) owned.Add(stack); 
                records.Add(WorldEntryService.BuildItemRecord126(landed[0]));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            
            Logger.LogWarning(ex, "{Name}: bag read for web mall entry {Entry} failed after {Landed} unit(s)",
                player.Name, entryId, records.Count);
        }

        if (records.Count < units)
        {
            await store.ReturnUnitsAsync(player.AccountId, taken, units - records.Count, ct);
            Logger.LogInformation("{Name}: {Left} of {Units} unit(s) of web mall entry {Entry} stay in the box (the bag is full)",
                player.Name, units - records.Count, units, entryId);
        }
        if (records.Count == 0) return;

        
        
        await services.GetRequiredService<LootService>().AdvanceCollectQuestsAsync(session, player, taken.ItemId, records.Count);
        await KharaService.OnHeldItemChangedAsync(session, player, taken.ItemId, services, Logger, ct);

        double filed = CashMallPackets.WallClockOleDate(taken.FiledUtc);
        foreach (var record in records)
            await session.SendRmiAsync(ZoneS2COpcodes.AnsWebItemAtMyInven,
                CashMallPackets.Build2641(entryId, taken.Box, filed, taken.SenderName, record), ct);

        Logger.LogInformation("{Name} moved {Count}x item {Item} from web mall entry {Entry} (box {Box}) into the bag",
            player.Name, records.Count, taken.ItemId, entryId, taken.Box);

        await CardRegistrationNotifier.NotifyArrivedAsync(session, player.CharacterId, new[] { taken.ItemId }, services, Logger);
    }
}
