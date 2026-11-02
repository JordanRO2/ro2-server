using System.Collections.Concurrent;

namespace RO2.Quests.Domain;

/// <summary>The fail columns of one Quest_Info row, as the planner reads them.</summary>
public sealed record FailRow(byte FailType, uint Value1, uint Value2);

/// <summary>One failure to send: the quest, the failing row's type and the 2434 tail.</summary>
public readonly record struct QuestFailure(uint QuestId, byte FailType, uint Tail);

/// <summary>
/// A login's kill table: (quest, row index) -> kills of the row's NPC since the quest was accepted. Wraps the map it is
/// given, so the owner keeps the storage and sees every count.
/// </summary>
public sealed class KillCounter(ConcurrentDictionary<(uint Quest, int Row), int> counts)
{
    public KillCounter() : this(new ConcurrentDictionary<(uint Quest, int Row), int>()) { }

    /// <summary>Counts one more kill against the row and returns the new total.</summary>
    public int Increment(uint quest, int row) => counts.AddOrUpdate((quest, row), 1, (_, n) => n + 1);
}

/// <summary>
/// Pure planning of the server-detected quest failures: which held quests a death fails (Fail_Type 26) and which a
/// kill of an NPC completes the fail count of (Fail_Type 2). Reads rows passed in; no disk, no state but the counter.
/// </summary>
public static class FailPlanner
{
    /// <summary>Quest_Info Fail_Type: killing the NPC Value1 Value2 times fails the quest.</summary>
    public const byte FailTypeKill = 2;

    /// <summary>Quest_Info Fail_Type: the player dying fails the quest.</summary>
    public const byte FailTypeDeath = 26;

    /// <summary>The tail of a Fail_Type 26 failure.</summary>
    public const uint DeathTail = 0;

    /// <summary>
    /// The held quests that a death fails: those with a Fail_Type 26 row that have not already failed, ascending,
    /// each with tail <see cref="DeathTail"/>.
    /// </summary>
    public static List<QuestFailure> PlanDeathFailures(IReadOnlyDictionary<uint, FailRow[]> rows,
        IEnumerable<uint> held, Func<uint, bool> alreadyFailed)
    {
        var plan = new List<QuestFailure>();
        foreach (var questId in held.Distinct().OrderBy(q => q))
        {
            if (alreadyFailed(questId) || !rows.TryGetValue(questId, out var questRows)) continue;
            if (questRows.Any(r => r.FailType == FailTypeDeath))
                plan.Add(new QuestFailure(questId, FailTypeDeath, DeathTail));
        }
        return plan;
    }

    /// <summary>
    /// Counts a kill of <paramref name="npcId"/> against the held quests' Fail_Type 2 rows that name it. Returns the
    /// quests whose count reached the row's Value2 (at least 1), ascending, each with the row's Value1 as tail.
    /// Quests the caller reports as failed are not counted; past the threshold the count keeps growing and the quest
    /// is reported again on every further kill until the caller marks it failed.
    /// </summary>
    public static List<QuestFailure> CountKill(IReadOnlyDictionary<uint, FailRow[]> rows, IEnumerable<uint> held,
        Func<uint, bool> alreadyFailed, KillCounter counter, uint npcId)
    {
        var plan = new List<QuestFailure>();
        if (npcId == 0) return plan;
        foreach (var questId in held.Distinct().OrderBy(q => q))
        {
            if (alreadyFailed(questId) || !rows.TryGetValue(questId, out var questRows)) continue;
            for (int i = 0; i < questRows.Length; i++)
            {
                var row = questRows[i];
                if (row.FailType != FailTypeKill || row.Value1 != npcId) continue;
                var killed = counter.Increment(questId, i);
                if (killed >= Math.Max(1u, row.Value2))
                {
                    plan.Add(new QuestFailure(questId, FailTypeKill, row.Value1));
                    break;
                }
            }
        }
        return plan;
    }
}
