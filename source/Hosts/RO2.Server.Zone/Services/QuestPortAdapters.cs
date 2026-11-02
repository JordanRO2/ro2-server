using RO2.GameData;
using RO2.Quests.Application;

namespace RO2.Server.Zone.Services;

/// <summary>Host adapter of <see cref="IQuestGameData"/> over the loaded Quest_Info and item tables.</summary>
public sealed class QuestGameData(GameDataStore data) : IQuestGameData
{
    public bool TryGetQuest(uint questId, out QuestData quest) => data.Quests.TryGetValue(questId, out quest!);

    public bool TryGetObjectiveRows(uint questId, out QuestData[] rows) => data.QuestRows.TryGetValue(questId, out rows!);

    public bool ItemExists(uint itemId) => data.Items.ContainsKey(itemId);
}
