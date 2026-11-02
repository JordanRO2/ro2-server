using RO2.GameData;

namespace RO2.Quests.Domain;

/// <summary>
/// Pure rules of a Quest_Info objective row's shape: which Complete_Type means what, how many rows are objectives, and
/// the totals a kill or use-item row needs. Reads <see cref="QuestData"/> only; no state, no persistence, no wire.
/// </summary>
public static class ObjectiveRules
{
    /// <summary>Quest_Info Complete_Type of a kill objective (Complete_Type_Value1 = NPC, Value2 = count).</summary>
    public const byte KillObjective = 2;

    /// <summary>Quest_Info Complete_Type of a row with no objective (the quest's non-objective rows); it is skipped.</summary>
    public const byte NoObjective = 0;

    /// <summary>Quest_Info Complete_Type of a talk objective (Complete_Type_Value1 = the NPC).</summary>
    public const byte TalkObjective = 1;

    /// <summary>Quest_Info Complete_Type of a collect objective (Value1 = the item, Value2 = the count).</summary>
    public const byte CollectObjective = 4;

    /// <summary>Quest_Info Complete_Type of a deliver objective (Value1 = the item, Value2 = the count).</summary>
    public const byte DeliverObjective = 5;

    /// <summary>Quest_Info Complete_Type of a use-item objective (Value2 = the item, Value3 = the uses).</summary>
    public const byte UseItemObjective = 6;

    /// <summary>Most uses a use-item row can need (the session counter is a byte).</summary>
    public const uint MaxUsesNeeded = 255;

    /// <summary>The quest's objective rows: the Quest_Info rows with Complete_Type != 0.</summary>
    public static int ObjectiveRowCount(QuestData[] rows)
    {
        int n = 0;
        foreach (var row in rows)
            if (row.Complete_Type != NoObjective) n++;
        return n;
    }

    /// <summary>
    /// A kill row's needed total as the session counters hold it: Complete_Type_Value2, with 0 read as 1 and the value
    /// capped at the u16 maximum.
    /// </summary>
    public static ushort NeededKills(QuestData row)
        => row.Complete_Type_Value2 == 0 ? (ushort)1 : (ushort)Math.Min(row.Complete_Type_Value2, ushort.MaxValue);

    /// <summary>A kill row that a kill can advance: Complete_Type 2 with a target (213010440 has none).</summary>
    public static bool IsKillRow(QuestData row) => row.Complete_Type == KillObjective && row.Complete_Type_Value1 != 0;

    /// <summary>Uses a use-item row needs: Complete_Type_Value3, with 0 read as 1 and the value capped at 255.</summary>
    public static ushort UsesNeeded(QuestData row) => (ushort)Math.Clamp(row.Complete_Type_Value3, 1u, MaxUsesNeeded);
}
