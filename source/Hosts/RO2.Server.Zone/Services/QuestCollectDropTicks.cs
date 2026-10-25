using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.GameData;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Inventory.Domain;

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
 /// <summary>Quest_Info Complete_Type of a collect objective (Value1 = item, Value2 = how many).</summary>
    private const byte CollectObjective = 4;

 /// <summary>Quest_Info Complete_Type of a deliver objective (Value1 = item handed over, Value2 = how many).
 /// Its item is ticked like a collect item.</summary>
    private const byte DeliverObjective = 5;

 /// <summary>One Complete_Type-4 or -5 row: its quest, its index among the quest's Quest_Info rows, and its need
 /// (<see cref="QuestPrizeHandler.ObjectiveItemCount"/>).</summary>
    internal readonly record struct CollectRow(uint QuestId, int RowIndex, int Need);

 /// <summary>One 2428 to send.</summary>
    internal readonly record struct Tick(uint QuestId, int RowIndex, ushort Count);

 /// <summary>item template -> its collect rows, built once per loaded QuestRows table.</summary>
    private static readonly ConditionalWeakTable<IReadOnlyDictionary<uint, QuestData[]>, Dictionary<uint, CollectRow[]>> Indexes = new();

 /// <summary>Every Complete_Type-4 or -5 row, in any quest, whose Complete_Type_Value1 is <paramref name="itemId"/>.</summary>
    internal static IReadOnlyList<CollectRow> RowsCollecting(IReadOnlyDictionary<uint, QuestData[]> questRows, uint itemId)
        => Indexes.GetValue(questRows, BuildIndex).TryGetValue(itemId, out var rows) ? rows : Array.Empty<CollectRow>();

    private static Dictionary<uint, CollectRow[]> BuildIndex(IReadOnlyDictionary<uint, QuestData[]> questRows)
    {
        var byItem = new Dictionary<uint, List<CollectRow>>();
        foreach (var (questId, rows) in questRows)
        {
            for (int i = 0; i < rows.Length; i++)
            {
                var row = rows[i];
                if (row.Complete_Type is not (CollectObjective or DeliverObjective) || row.Complete_Type_Value1 == 0) continue;
                if (!byItem.TryGetValue(row.Complete_Type_Value1, out var list))
                    byItem[row.Complete_Type_Value1] = list = new List<CollectRow>();
                list.Add(new CollectRow(questId, i, QuestPrizeHandler.ObjectiveItemCount(row)));
            }
        }
        return byItem.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
    }

 /// <summary>
 /// <summary>
 /// The 2428s owed when the bag holds <paramref name="heldInBag"/> of the item after a removal: one per row of
 /// <paramref name="rows"/> whose quest is in <paramref name="heldQuests"/>, in ascending quest-id order, each with
 /// <c>min(heldInBag, need)</c>.
 /// </summary>
 /// </summary>
    internal static List<Tick> PlanTicks(IReadOnlyList<CollectRow> rows, IReadOnlyCollection<uint> heldQuests, long heldInBag)
    {
        var ticks = new List<Tick>();
        foreach (var row in rows.OrderBy(r => r.QuestId).ThenBy(r => r.RowIndex))
        {
            if (!heldQuests.Contains(row.QuestId)) continue;
            ticks.Add(new Tick(row.QuestId, row.RowIndex, (ushort)Math.Clamp(Math.Min(heldInBag, row.Need), 0, ushort.MaxValue)));
        }
        return ticks;
    }

 /// <summary>
 /// Lowers a first row's collect counter to a tick's count when the counter is above it. It never raises the counter or
 /// creates one. Rows after the first keep no counter on this server. A deliver first row is lowered the same way.
 /// </summary>
    internal static void LowerFirstRowCounter(PlayerState player, Tick tick)
    {
        
        if (tick.RowIndex == 0 && player.QuestKillProgress.TryGetValue(tick.QuestId, out var counted) && counted > tick.Count)
            player.QuestKillProgress[tick.QuestId] = tick.Count;
    }

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
        var inBag = QuestPrizeHandler.CountInBag(bag, itemId);
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
