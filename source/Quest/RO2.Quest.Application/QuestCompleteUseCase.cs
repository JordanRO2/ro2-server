using RO2.GameData;
using RO2.Quests.Domain;

namespace RO2.Quests.Application;

/// <summary>Which gate stopped a 1377, or <see cref="Closed"/> when the report is accepted; the host maps each verdict to its wire.</summary>
public enum QuestCompleteVerdict
{
    /// <summary>The report is accepted: the host applies <see cref="QuestCompleteResult.Counter"/> and runs its sends.</summary>
    Closed,

    /// <summary>The quest is not in Quest_Info: 2431 insert-rejected.</summary>
    UnknownQuest,

    /// <summary>The player does not hold the quest: 2431 prize-rejected.</summary>
    NotHeld,
}

/// <summary>What the report does to the quest's session counter.</summary>
public enum QuestCounterChange
{
    /// <summary>The counter is left as it is (collect, kill and use-item quests).</summary>
    None,

    /// <summary>The quest is paid now: the host drops it from the active set and forgets every counter.</summary>
    Forget,

    /// <summary>The counter is set to <see cref="QuestCompleteResult.CounterValue"/>.</summary>
    Set,
}

/// <summary>
/// The decision of a 1377 for one quest. <see cref="SendUpdate"/> is the 2432 (first talk report), <see cref="ClosesNow"/>
/// is the moment the objective-close grants are handed out, <see cref="PayNow"/> is a Reward_Auto talk quest.
/// </summary>
public readonly record struct QuestCompleteResult(QuestCompleteVerdict Verdict, QuestData? Quest = null, bool PayNow = false,
    bool SendUpdate = false, bool ClosesNow = false, QuestCounterChange Counter = QuestCounterChange.None, ushort CounterValue = 0);

/// <summary>What the host knows about the request: whether the player holds the quest, its counter, and the Reward_Auto column.</summary>
public readonly record struct QuestCompleteRequest(uint QuestId, bool Held, ushort Progress, bool RewardAuto);

/// <summary>
/// The objective-complete decision of a 1377 naming a quest: unknown quest, not held, then the per-type counter. A talk
/// quest is closed to 1 (or paid on the spot with Reward_Auto); a deliver quest keeps max(progress, byte-capped total);
/// any other type leaves its counter alone. The host applies the counter, keeps the wire and the bag IO, and persists the
/// payout through <see cref="CompleteAsync"/>.
/// </summary>
public sealed class QuestCompleteUseCase(IQuestRepository quests, IQuestGameData data)
{
    /// <summary>Runs the unknown and not-held gates, then picks the per-type counter change.</summary>
    public QuestCompleteResult Decide(QuestCompleteRequest request)
    {
        if (!data.TryGetQuest(request.QuestId, out var quest)) return new(QuestCompleteVerdict.UnknownQuest);
        if (!request.Held) return new(QuestCompleteVerdict.NotHeld, quest);

        var isTalk = quest.Complete_Type == ObjectiveRules.TalkObjective;
        var payNow = isTalk && request.RewardAuto;
        var sendUpdate = isTalk && request.Progress == 0;

        // The deliver total is byte-capped, while the counter it is compared with is a u16: a progress above 255 is kept.
        var deliverTotal = quest.Complete_Type_Value2 == 0 ? (byte)1 : (byte)Math.Min(quest.Complete_Type_Value2, 255);
        var isDeliver = quest.Complete_Type == ObjectiveRules.DeliverObjective;
        var closesNow = sendUpdate || (isDeliver && request.Progress < deliverTotal);

        var (counter, value) = payNow ? (QuestCounterChange.Forget, (ushort)0)
            : isTalk ? (QuestCounterChange.Set, (ushort)1)
            : isDeliver ? (QuestCounterChange.Set, Math.Max(request.Progress, (ushort)deliverTotal))
            : (QuestCounterChange.None, (ushort)0);
        return new(QuestCompleteVerdict.Closed, quest, payNow, sendUpdate, closesNow, counter, value);
    }

    /// <summary>Persists the completion of a quest paid on the spot.</summary>
    public Task CompleteAsync(uint characterId, uint questId) => quests.CompleteQuestAsync(characterId, questId);
}
