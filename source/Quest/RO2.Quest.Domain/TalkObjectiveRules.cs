using RO2.GameData;

namespace RO2.Quests.Domain;

/// <summary>
/// Pure rules of the talk objectives a quest tracks per login: which Quest_Info rows are tracked, and what meeting an NPC
/// does to the met set. The live registry, the 2432 wire and the save path stay in the host.
/// </summary>
public static class TalkObjectiveRules
{
    /// <summary>The outcome of meeting one NPC: the tracked rows it closes, and whether every tracked row is met after it.</summary>
    public readonly record struct MeetResult(int[] Targets, bool AllMet);

    /// <summary>
    /// The indexes of the talk rows tracked for a quest: its talk rows with Complete_Type_Value3 0 when there are at least
    /// two of them, or the single talk row of a quest whose NPC is not its reward NPC. Otherwise none.
    /// </summary>
    /// <remarks>
    /// A single-talk quest whose turn-in NPC differs from its talk NPC cannot be turned in until the talk NPC has been met
    /// in this login. Meeting the talk NPC closes its row, as it does for a multi-talk quest. A talk row that names the
    /// reward NPC is left to the turn-in, which meets it.
    /// </remarks>
    public static int[] TrackedRows(QuestData[] rows)
    {
        var tracked = new List<int>(rows.Length);
        for (int i = 0; i < rows.Length; i++)
            if (rows[i].Complete_Type == ObjectiveRules.TalkObjective && rows[i].Complete_Type_Value3 == 0 && rows[i].Complete_Type_Value1 != 0)
                tracked.Add(i);
        if (tracked.Count >= 2) return tracked.ToArray();
        if (tracked.Count == 1 && tracked[0] == 0 && rows.Skip(1).All(r => r.Complete_Type == 0)
            && rows[0].Complete_Type_Value1 != rows[0].Reward_NPC_ID)
            return tracked.ToArray();
        return [];
    }

    /// <summary>
    /// What meeting <paramref name="npcAsset"/> does: the tracked rows that name it, and AllMet over <paramref name="met"/>
    /// plus those rows. Null when no tracked row names the NPC (nothing to record). <paramref name="met"/> is not changed;
    /// the caller unions <see cref="MeetResult.Targets"/> into its own set.
    /// </summary>
    public static MeetResult? MeetDecision(QuestData[] rows, int[] tracked, IReadOnlySet<int>? met, uint npcAsset)
    {
        var targets = tracked.Where(i => rows[i].Complete_Type_Value1 == npcAsset).ToArray();
        if (targets.Length == 0) return null;
        return new MeetResult(targets, tracked.All(i => targets.Contains(i) || (met?.Contains(i) ?? false)));
    }
}
