using RO2.GameData;
using RO2.Quests.Domain;

namespace RO2.Quests.Application;

/// <summary>Whether a 1375 abandons a held quest or is refused; the host maps each verdict to its wire.</summary>
public enum QuestGiveUpVerdict
{
    /// <summary>The character did not hold the quest: 2420 refused with 21711.</summary>
    NotHeld,

    /// <summary>The character held the quest: the host takes back the accept items, then <see cref="QuestGiveUpUseCase.GiveUpAsync"/>.</summary>
    GaveUp,
}

/// <summary>Result of the give-up decision. <see cref="Quest"/> is the Quest_Info row of a held quest (null when unknown or not held).</summary>
public readonly record struct QuestGiveUpResult(QuestGiveUpVerdict Verdict, QuestData? Quest = null);

/// <summary>
/// The give-up decision of a 1375. the <c>wasHeld</c> argument is the result of the host's own removal from the live
/// active set: the host wipes every counter BEFORE asking (so a refused give-up still erases a stray counter), and the
/// use case must be called after that wipe, never before.
/// </summary>
public sealed class QuestGiveUpUseCase(IQuestRepository quests, IQuestGameData data)
{
    /// <summary>Refuses a quest the character did not hold; for a held one, returns its Quest_Info row for the host's take-back.</summary>
    public QuestGiveUpResult Decide(uint questId, bool wasHeld)
    {
        if (!wasHeld) return new(QuestGiveUpVerdict.NotHeld);
        return new(QuestGiveUpVerdict.GaveUp, data.TryGetQuest(questId, out var quest) ? quest : null);
    }

    /// <summary>Persists the give-up once the host has taken back the accept items.</summary>
    public Task GiveUpAsync(uint characterId, uint questId) => quests.GiveUpQuestAsync(characterId, questId);
}
