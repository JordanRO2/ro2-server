using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.GameData;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Pays out a gathered function object: rolls its FuncReward rows and places the items in the bag through the same
/// landing path as a ground pickup. Each item is announced with its collect-quest updates and its inventory notification.
/// </summary>
/// <remarks>
/// <para>
/// The payout is all or nothing. If the rolled items do not fit, nothing is paid and the bag-full message is sent. The
/// whole payout is planned against the bag before anything is written.
/// </para>
/// <para>
/// A gather on an object of Action_Type 11 that adds at least one stack also grants profession experience after the item
/// notifications. The gain is Action_Value1, or Action_Value1 multiplied by 1.3 and rounded down. A refused or empty
/// gather grants none.
/// </para>
/// <para>
/// The order sent to the client is: the state update, then per item its quest and item notifications, then the
/// completion messages.
/// </para>
/// </remarks>
public static class FuncObjectActionService
{
 /// <summary>
 /// Rolls and pays <paramref name="templateId"/>'s FuncReward rows to <paramref name="player"/>. Returns the system message
 /// id that the completion messages carry: 0, or the bag-full message id when the rolled items do not fit, in which case
 /// nothing is paid. A template with no FuncReward row, or a roll that pays no row, pays nothing and returns 0.
 /// </summary>
    public static async Task<uint> PayOutAsync(ProudSession session, PlayerState player, uint templateId,
        IServiceProvider services, ILogger logger, Random random, CancellationToken ct)
    {
        var store = services.GetRequiredService<GameDataStore>();
        var rolled = new List<(uint ItemId, int Count)>();
        foreach (var (itemId, count) in FuncObjectActions.Roll(FuncObjectActions.RewardsOf(store, templateId), random))
        {
            
            if (store.Items.ContainsKey(itemId)) rolled.Add((itemId, count));
            else logger.LogWarning("{Name}: function object {Template} rolled item {Item}, which is not in ItemInfo, not paid",
                player.Name, templateId, itemId);
        }
        if (rolled.Count == 0) return 0;

        var inventory = services.GetRequiredService<IInventoryRepository>();
        var owned = new List<RO2.Domain.Entities.InventoryItem>(await inventory.GetByCharacterAsync(player.CharacterId));
        var plans = new List<(uint ItemId, int Count, ItemPickUpHandler.Landing Landing)>(rolled.Count);
        foreach (var (itemId, count) in rolled)
        {
            var landing = ItemPickUpHandler.PlanLanding(owned, player.CharacterId, itemId, count, store.Items);
            if (landing.Refused)
            {
                logger.LogInformation("{Name}: function object {Template} payout does not fit the bag ({Count}x {Item}), nothing paid",
                    player.Name, templateId, count, itemId);
                return FuncObjectRewardPackets.BagFullMessageId;
            }
            
            
            owned.AddRange(landing.NewStacks);
            plans.Add((itemId, count, landing));
        }

        var loot = services.GetRequiredService<LootService>();
        var anyLanded = false;
        foreach (var (itemId, count, landing) in plans)
        {
            var landed = await ItemPickUpHandler.WriteLandingAsync(inventory, landing, player, itemId, logger);
            if (landed.Count == 0) continue;
            anyLanded = true;

            
            
            await loot.AdvanceCollectQuestsAsync(session, player, itemId, count);
            await KharaService.OnHeldItemChangedAsync(session, player, itemId, services, logger, ct);
            foreach (var stack in landed)
                await session.SendRmiAsync(ZoneS2COpcodes.OnPickupItem,
                    LootNotifyPackets.BuildPickupItem2333(WorldEntryService.BuildItemRecord126(stack)), ct);
            logger.LogInformation("{Name} gathered {Count}x {Item} from function object {Template}",
                player.Name, count, itemId, templateId);
        }

        
        
        if (anyLanded && FuncObjectActions.ProfessionExpOf(store, templateId) is var baseExp and > 0)
        {
            var gain = EffectService.ApplyExpRate(baseExp, services.GetRequiredService<EffectService>().ProJobExpRateOf(player));
            await ProfessionExp.GrantAsync(session, player, gain, store, services.GetRequiredService<BroadcastService>(),
                services.GetRequiredService<ICharacterRepository>(), logger, services);
        }
        return 0;
    }
}
