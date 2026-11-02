using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.GameData;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Inventory.Domain;
using RO2.Quests.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// 2428 OnQuestInCompleteNotify when a collect or deliver objective's item LEAVES the bag: one tick per held quest
/// whose Complete_Type-4 (collect) or Complete_Type-5 (deliver) row names the item, carrying the count the bag still
/// holds, capped at the row's need.
/// </summary>
/// <remarks>
/// A tick is sent when an item held for a collect or deliver objective leaves the bag (destroyed, sold, offered or
/// given away). Each held quest whose objective names the item gets a tick carrying the count still in the bag,
/// capped at the objective's need, so a quest whose items are gone is shown as not done. Turn-in removals send no tick.
/// The first row's collect counter is lowered to the ticked count, never raised, so that a re-pickup can count again.
/// The tick is sent after the inventory write and before the change's own item message.
/// Known edge: a collect quest whose objective-close row grants the collected item can close its objective a second
/// time when the item is dropped and collected again.
/// </remarks>
internal static class QuestCollectDropTicks
{
 /// <summary>item template -> its collect rows, built once per loaded QuestRows table.</summary>
    private static readonly ConditionalWeakTable<IReadOnlyDictionary<uint, QuestData[]>, Dictionary<uint, CollectDropTickPlanner.CollectRow[]>> Indexes = new();

 /// <summary>Every Complete_Type-4 or -5 row, in any quest, whose Complete_Type_Value1 is <paramref name="itemId"/>.</summary>
    internal static IReadOnlyList<CollectDropTickPlanner.CollectRow> RowsCollecting(IReadOnlyDictionary<uint, QuestData[]> questRows, uint itemId)
        => Indexes.GetValue(questRows, BuildIndex).TryGetValue(itemId, out var rows) ? rows : Array.Empty<CollectDropTickPlanner.CollectRow>();

    private static Dictionary<uint, CollectDropTickPlanner.CollectRow[]> BuildIndex(IReadOnlyDictionary<uint, QuestData[]> questRows)
        => CollectDropTickPlanner.BuildIndex(questRows, QuestObjectiveItems.ObjectiveItemCount);

    internal static List<CollectDropTickPlanner.Tick> PlanTicks(IReadOnlyList<CollectDropTickPlanner.CollectRow> rows, IReadOnlyCollection<uint> heldQuests, long heldInBag)
        => CollectDropTickPlanner.PlanTicks(rows, heldQuests, heldInBag);

    internal static void LowerFirstRowCounter(PlayerState player, CollectDropTickPlanner.Tick tick)
        => CollectDropTickPlanner.LowerFirstRowCounter(player.QuestKillProgress, tick);

 /// <summary>
 /// Some of item <paramref name="itemId"/> left <paramref name="player"/>'s bag (destroyed, sold or offered, and
 /// already written): sends the 2428 of every held quest collecting or delivering it and lowers the first-row
 /// counters to match. Returns at once, reading nothing, when no quest names the item.
 /// </summary>
    internal static async Task OnCollectItemLeftBagAsync(ProudSession session, PlayerState player, uint itemId,
        IServiceProvider services, ILogger log, CancellationToken ct)
    {
        var rows = RowsCollecting(services.GetRequiredService<GameDataStore>().QuestRows, itemId);
        if (rows.Count == 0) return;
        
        var held = player.ActiveQuests.ToArray();
        if (!rows.Any(r => held.Contains(r.QuestId))) return;

        var bag = await services.GetRequiredService<IInventoryRepository>().GetByCharacterAsync(player.CharacterId);
        var inBag = QuestObjectiveItems.CountInBag(bag, itemId);
        foreach (var tick in PlanTicks(rows, held, inBag))
        {
            LowerFirstRowCounter(player, tick);

            var body = QuestItemObjectivePackets.BuildQuestInCompleteNotify2428(tick.QuestId, itemId, tick.Count);
            await session.SendRmiAsync(ZoneS2COpcodes.OnQuestInCompleteNotify, writer => writer.WriteBytes(body), ct);
            log.LogInformation("{Name} quest {QuestId}: collect item {Item} left the bag, {Count} held",
                player.Name, tick.QuestId, itemId, tick.Count);
        }
    }
}
