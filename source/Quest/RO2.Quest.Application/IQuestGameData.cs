using RO2.GameData;

namespace RO2.Quests.Application;

/// <summary>
/// The Quest_Info catalog the quest use cases read, behind a port so they do not reach the host GameDataStore.
/// Each member mirrors a lookup the Accept, Complete, GiveUp and Prize flows make today.
/// </summary>
public interface IQuestGameData
{
    /// <summary>The first Quest_Info row of the quest (the one the flows treat as the quest itself); false when the quest is unknown.</summary>
    bool TryGetQuest(uint questId, out QuestData quest);

    /// <summary>
    /// Every Quest_Info row of the quest in file order (one per objective); false when the quest has none.
    /// Callers that fall back to the single quest row when this is false do so themselves.
    /// </summary>
    bool TryGetObjectiveRows(uint questId, out QuestData[] rows);

    /// <summary>True when the item template exists in the item catalog (the reward payout skips unknown ids).</summary>
    bool ItemExists(uint itemId);
}
