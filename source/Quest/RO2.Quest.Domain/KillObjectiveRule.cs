using RO2.GameData;

namespace RO2.Quests.Domain;

/// <summary>
/// Pure kill-objective decisions: whether one kill advances a row's count, and whether every objective row of a quest is
/// met. Where the counts live (first-row counter vs later-row per-NPC counts) is the caller's, passed in as a lookup.
/// </summary>
public static class KillObjectiveRule
{
    /// <summary>A count already at its need is not ticked; otherwise it goes up by one.</summary>
    public static (ushort NewCount, bool Ticked) Advance(ushort current, ushort needed)
        => current >= needed ? (current, false) : ((ushort)(current + 1), true);

    /// <summary>
    /// True when the quest has at least one objective row, every objective row is a kill row with a target, and each row's
    /// count (<paramref name="countOf"/> gets the row index and row) has reached its need.
    /// </summary>
    public static bool AllKillObjectivesMet(QuestData[] rows, Func<int, QuestData, ushort> countOf)
    {
        var any = false;
        for (int i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            if (row.Complete_Type == ObjectiveRules.NoObjective) continue;
            if (row.Complete_Type != ObjectiveRules.KillObjective || row.Complete_Type_Value1 == 0) return false;
            if (countOf(i, row) < ObjectiveRules.NeededKills(row)) return false;
            any = true;
        }
        return any;
    }
}
