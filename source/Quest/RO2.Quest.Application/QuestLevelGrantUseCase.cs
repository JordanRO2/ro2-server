using RO2.GameData;
using RO2.Quests.Domain;

namespace RO2.Quests.Application;

/// <summary>
/// The decision of the level quests a quest payout starts by itself (Start_Type 4): selects those due at the new level
/// (<see cref="QuestLevelGrantRules.SelectDue"/>), drops the ones the character has completed or now holds, and persists
/// the accept of each. The host keeps the live run-state, the 2421 insert and the ordering, through the callback.
/// </summary>
public sealed class QuestLevelGrantUseCase(IQuestRepository quests)
{
    /// <summary>
    /// Accepts, in ascending quest-id order, every due quest the character has not completed and does not hold, and calls
    /// <paramref name="onAccepted"/> after each accept persists. <paramref name="held"/> is re-read per quest (the callback
    /// awaits, so the live set can change). Nothing happens, and the repository is not read, when no quest is due.
    /// </summary>
    public async Task GrantAsync(uint characterId, IEnumerable<QuestData> catalog, Func<uint, bool> held,
        Func<uint, ProJobRange?> proJobOf, byte level, byte jobLevel, Func<QuestData, Task> onAccepted)
    {
        var due = QuestLevelGrantRules.SelectDue(catalog, held, proJobOf, level, jobLevel);
        if (due.Count == 0) return;

        var completed = await quests.GetCompletedQuestIdsAsync(characterId);
        foreach (var quest in due)
        {
            if (completed.Contains(quest.Quest_ID) || held(quest.Quest_ID)) continue;
            await quests.AcceptQuestAsync(characterId, quest.Quest_ID);
            await onAccepted(quest);
        }
    }
}
