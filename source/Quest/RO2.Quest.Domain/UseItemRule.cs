using RO2.GameData;

namespace RO2.Quests.Domain;

/// <summary>
/// Pure use-item-objective decisions: how one use advances a row's count, and whether every objective row of a quest is a
/// met use-item row. Where the counts live (the per-row table of the player) is the caller's, passed in as a lookup.
/// </summary>
public static class UseItemRule
{
    /// <summary>The count after one use, capped at <paramref name="needed"/>; null when the row was already met.</summary>
    public static ushort? Advance(ushort count, ushort needed)
        => count >= needed ? null : (ushort)Math.Min(count + 1, needed);

    /// <summary>
    /// True when every objective row (Complete_Type != 0) is a use-item row whose count (<paramref name="countOf"/> gets the
    /// row index) has reached <see cref="ObjectiveRules.UsesNeeded"/>. Unlike the kill rule, a quest with no objective row is
    /// vacuously met: the caller only asks after a use ticked a row.
    /// </summary>
    public static bool AllUseItemRowsMet(QuestData[] rows, Func<int, int> countOf)
    {
        for (int i = 0; i < rows.Length; i++)
        {
            if (rows[i].Complete_Type == ObjectiveRules.NoObjective) continue;
            if (rows[i].Complete_Type != ObjectiveRules.UseItemObjective
                || countOf(i) < ObjectiveRules.UsesNeeded(rows[i])) return false;
        }
        return true;
    }
}
