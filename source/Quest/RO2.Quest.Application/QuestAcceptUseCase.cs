using RO2.GameData;
using RO2.Quests.Domain;

namespace RO2.Quests.Application;

/// <summary>Which accept gate stopped a 1374, or <see cref="Accepted"/> when gates 1-5 pass; the host maps each verdict to its wire.</summary>
public enum QuestAcceptVerdict
{
    /// <summary>Gates 1-5 passed: the host runs the bag gates, then <see cref="QuestAcceptUseCase.AcceptAsync"/>.</summary>
    Accepted,

    /// <summary>The quest is not in Quest_Info: 2431 insert-rejected.</summary>
    UnknownQuest,

    /// <summary>The player already holds the quest: 2431 insert-rejected.</summary>
    AlreadyActive,

    /// <summary>A one-shot quest the character already completed: ignored, no wire.</summary>
    OneShotDone,

    /// <summary>A daily quest already done since the last reset: 2431 daily-done carrying <see cref="QuestAcceptResult.DailyQuest"/>.</summary>
    DailyDone,

    /// <summary>The class / profession / Job_Master gate refused: 2431 carrying <see cref="QuestAcceptResult.GateCode"/>.</summary>
    GateRefused,
}

/// <summary>Result of the accept decision. <see cref="Quest"/> is set from the unknown-quest gate on.</summary>
public readonly record struct QuestAcceptResult(QuestAcceptVerdict Verdict, QuestData? Quest = null, uint GateCode = 0, byte DailyQuest = 0);

/// <summary>The character facts and Quest_Info gate columns of gate 5, read by the host; null in the request skips the gate.</summary>
public readonly record struct QuestAcceptGateInput(AcceptGateColumns Columns, byte CombatClass, byte Profession, byte BaseLevel, byte MasterLevel);

/// <summary>
/// What the host knows about the request. <paramref name="IsOneShot"/> is Quest_Frequency 0; <paramref name="DailyQuest"/>
/// is the Daily_Quest column (0 = not a daily quest). The use case touches no player state or game-data table.
/// </summary>
public readonly record struct QuestAcceptRequest(uint CharacterId, uint QuestId, bool AlreadyActive, bool IsOneShot,
    byte DailyQuest, DateTime NowUtc, QuestAcceptGateInput? Gate);

/// <summary>
/// The accept decision of a 1374 quest-accept: unknown quest, already active, one-shot done, daily done, then the
/// class / profession / Job_Master gate, in that order. The bag gates (start item, accept items, free cells) need the
/// bag and stay in the host between <see cref="DecideAsync"/> and <see cref="AcceptAsync"/>; the host also keeps the wire.
/// </summary>
public sealed class QuestAcceptUseCase(IQuestRepository quests, IQuestGameData data)
{
    /// <summary>Runs gates 1-5 in order; each stops at the first that refuses.</summary>
    public async Task<QuestAcceptResult> DecideAsync(QuestAcceptRequest request)
    {
        if (!data.TryGetQuest(request.QuestId, out var quest)) return new(QuestAcceptVerdict.UnknownQuest);
        if (request.AlreadyActive) return new(QuestAcceptVerdict.AlreadyActive, quest);

        if (request.IsOneShot
            && (await quests.GetCompletedQuestIdsAsync(request.CharacterId)).Contains(request.QuestId))
            return new(QuestAcceptVerdict.OneShotDone, quest);

        if (request.DailyQuest > 0
            && QuestRepeatRule.DoneToday(await quests.GetLastCompletedAtAsync(request.CharacterId, request.QuestId), request.NowUtc))
            return new(QuestAcceptVerdict.DailyDone, quest, DailyQuest: request.DailyQuest);

        if (request.Gate is { } g
            && QuestAcceptRules.Refusal(g.Columns, g.CombatClass, g.Profession, g.BaseLevel, g.MasterLevel) is { } code)
            return new(QuestAcceptVerdict.GateRefused, quest, GateCode: code);

        return new(QuestAcceptVerdict.Accepted, quest);
    }

    /// <summary>Persists the accept once every gate, including the host's bag gates, has passed.</summary>
    public Task AcceptAsync(uint characterId, uint questId) => quests.AcceptQuestAsync(characterId, questId);
}
