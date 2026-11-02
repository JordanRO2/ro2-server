using RO2.GameData;

namespace RO2.Quests.Domain;

/// <summary>The profession-level columns of a Quest_Info row, which QuestData does not carry. A Max of 0 = no bound.</summary>
public readonly record struct ProJobRange(byte Min, byte Max);

/// <summary>
/// Pure rules of the quests the server starts by itself when a payout raises the base level (Start_Type 4, started at
/// level Start_Type_ID). The base-level and profession-level maximums are INCLUSIVE here (Job_Master's is exclusive).
/// </summary>
public static class QuestLevelGrantRules
{
    /// <summary>Quest_Info Start_Type of a quest the server starts at a base level (Start_Type_ID).</summary>
    public const byte LevelStartType = 4;

    /// <summary>
    /// True when <paramref name="quest"/> is a Start_Type-4 quest whose start level, base-level range and profession-level
    /// range the character meets at <paramref name="level"/> and job level <paramref name="jobLevel"/>. A MAX of 0 is
    /// read as no upper bound.
    /// </summary>
    public static bool Meets(QuestData quest, ProJobRange proJob, byte level, byte jobLevel) =>
        quest.Start_Type == LevelStartType
        && level >= quest.Start_Type_ID
        && level >= quest.MIN_Base_Level && (quest.MAX_Base_Level == 0 || level <= quest.MAX_Base_Level)
        && jobLevel >= proJob.Min && (proJob.Max == 0 || jobLevel <= proJob.Max);

    /// <summary>
    /// The Start_Type-4 quests <paramref name="quests"/> that the character does not hold (<paramref name="held"/>), that
    /// have a profession range (<paramref name="proJobOf"/>) and that it meets (<see cref="Meets"/>), ascending by quest id.
    /// </summary>
    public static List<QuestData> SelectDue(IEnumerable<QuestData> quests, Func<uint, bool> held,
        Func<uint, ProJobRange?> proJobOf, byte level, byte jobLevel) =>
        quests
            .Where(q => q.Start_Type == LevelStartType && !held(q.Quest_ID)
                        && proJobOf(q.Quest_ID) is { } p && Meets(q, p, level, jobLevel))
            .OrderBy(q => q.Quest_ID)
            .ToList();
}
