using RO2.GameData;

namespace RO2.Quests.Domain;

/// <summary>
/// Pure turn-in predicates of a quest: the bag stacks a hand-over empties, the single-talk row shape, and the two
/// talk-NPC refusals. State (the player, the store, the bag) is read by the host and passed in as arguments.
/// </summary>
public static class QuestTurnInRules
{
    /// <summary><c>Complete_Type_Value3</c> of a talk row that is met by speaking to its NPC. A row with Value3 1 is
    /// closed by the client itself with a 1377.</summary>
    public const uint TalkRowMetByDialog = 0;

    /// <summary>
    /// The ids of the stacks that a hand-over of <paramref name="count"/> of <paramref name="itemId"/> empties, so the bag
    /// check can count those cells as free. <paramref name="bagStacks"/> are the main-bag stacks only, in bag order.
    /// </summary>
    public static HashSet<ulong> StacksEmptiedBy(IEnumerable<(ulong Id, uint ItemId, int Count)> bagStacks, uint itemId, int count)
    {
        var emptied = new HashSet<ulong>();
        var left = count;
        foreach (var stack in bagStacks)
        {
            if (left <= 0) break;
            if (stack.ItemId != itemId) continue;
            var held = Math.Max(1, stack.Count);
            if (held <= left) emptied.Add(stack.Id);
            left -= held;
        }
        return emptied;
    }

    /// <summary>True when the quest's only objective is its first row, a talk objective with an NPC.</summary>
    public static bool IsSingleTalkRow(QuestData[] rows) =>
        rows.Length > 0 && rows[0].Complete_Type == ObjectiveRules.TalkObjective && rows[0].Complete_Type_Value3 == TalkRowMetByDialog
        && rows[0].Complete_Type_Value1 != 0 && rows.Skip(1).All(r => r.Complete_Type == ObjectiveRules.NoObjective);

    /// <summary>
    /// True when a turn-in must be refused: the quest starts with a talk objective at another NPC (not the reward NPC, not
    /// the giver) that the player has not met in this login. <paramref name="isMeetableNpc"/> is evaluated last, only when
    /// every cheaper conjunct holds.
    /// </summary>
    public static bool TalkMetElsewhereUnmet(QuestData quest, uint giverId, bool objectiveClosed, bool acceptedThisLogin,
        Func<uint, bool> isMeetableNpc) =>
        quest.Complete_Type == ObjectiveRules.TalkObjective && quest.Complete_Type_Value1 != 0
        && quest.Complete_Type_Value1 != quest.Reward_NPC_ID && quest.Complete_Type_Value1 != giverId
        && !objectiveClosed && acceptedThisLogin && isMeetableNpc(quest.Complete_Type_Value1);

    /// <summary>
    /// True when the turn-in at <paramref name="giverId"/> also closes the held quest whose rows are <paramref name="rows"/>:
    /// a single-talk quest whose talk NPC is the giver, with no kill progress and not failed.
    /// </summary>
    public static bool ClosesWithTurnIn(QuestData[] rows, uint giverId, ushort progress, bool failed) =>
        IsSingleTalkRow(rows) && rows[0].Complete_Type_Value1 == giverId && progress == 0 && !failed;
}
