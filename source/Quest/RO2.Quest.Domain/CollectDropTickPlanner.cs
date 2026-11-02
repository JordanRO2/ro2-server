using RO2.GameData;

namespace RO2.Quests.Domain;

/// <summary>
/// Pure planning of the 2428 ticks owed when a collect or deliver objective's item leaves the bag: the item -> row
/// index, the tick per held quest, and the first-row counter guard. No state, no persistence, no wire.
/// </summary>
public static class CollectDropTickPlanner
{
    /// <summary>One Complete_Type-4 or -5 row: its quest, its index among the quest's Quest_Info rows, and its need.</summary>
    public readonly record struct CollectRow(uint QuestId, int RowIndex, int Need);

    /// <summary>One 2428 to send.</summary>
    public readonly record struct Tick(uint QuestId, int RowIndex, ushort Count);

    /// <summary>
    /// item template -> its Complete_Type-4 or -5 rows, in any quest, keyed by Complete_Type_Value1 (0 is never
    /// indexed). <paramref name="needOf"/> gives a row's need.
    /// </summary>
    public static Dictionary<uint, CollectRow[]> BuildIndex(IReadOnlyDictionary<uint, QuestData[]> questRows, Func<QuestData, int> needOf)
    {
        var byItem = new Dictionary<uint, List<CollectRow>>();
        foreach (var (questId, rows) in questRows)
        {
            for (int i = 0; i < rows.Length; i++)
            {
                var row = rows[i];
                if (row.Complete_Type is not (ObjectiveRules.CollectObjective or ObjectiveRules.DeliverObjective) || row.Complete_Type_Value1 == 0) continue;
                if (!byItem.TryGetValue(row.Complete_Type_Value1, out var list))
                    byItem[row.Complete_Type_Value1] = list = new List<CollectRow>();
                list.Add(new CollectRow(questId, i, needOf(row)));
            }
        }
        return byItem.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
    }

    /// <summary>
    /// The 2428s owed when the bag holds <paramref name="heldInBag"/> of the item after a removal: one per row of
    /// <paramref name="rows"/> whose quest is in <paramref name="heldQuests"/>, in ascending quest-id order, each with
    /// <c>min(heldInBag, need)</c>.
    /// </summary>
    public static List<Tick> PlanTicks(IReadOnlyList<CollectRow> rows, IReadOnlyCollection<uint> heldQuests, long heldInBag)
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
    public static void LowerFirstRowCounter(IDictionary<uint, ushort> counters, Tick tick)
    {
        if (tick.RowIndex == 0 && counters.TryGetValue(tick.QuestId, out var counted) && counted > tick.Count)
            counters[tick.QuestId] = tick.Count;
    }
}
