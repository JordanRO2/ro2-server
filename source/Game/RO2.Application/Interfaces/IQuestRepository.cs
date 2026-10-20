using RO2.Domain.Entities;

namespace RO2.Application.Interfaces;

public interface IQuestRepository
{
    Task<IReadOnlyList<CharacterQuest>> GetActiveQuestsAsync(uint characterId);
    Task<IReadOnlyList<uint>> GetCompletedQuestIdsAsync(uint characterId);

 /// <summary>When the character last completed <paramref name="questId"/> (UTC), or null if it never has. The zone's
 /// daily-quest gate (QuestRepeatGate) compares it with the last daily reset.</summary>
    Task<DateTime?> GetLastCompletedAtAsync(uint characterId, uint questId);
    Task AcceptQuestAsync(uint characterId, uint questId);
    Task CompleteQuestAsync(uint characterId, uint questId);
    Task GiveUpQuestAsync(uint characterId, uint questId);

 /// <summary>Replace the character's tracker-pinned ("lock/focus") set: mark the given in-progress quests
 /// traced and un-trace the rest. Persisted so the tracker survives relog (sent back via 2779 on login).</summary>
    Task SetQuestsTracedAsync(uint characterId, IReadOnlyCollection<uint> tracedQuestIds);

 /// <summary>Store the in-progress quest's objective progress (<see cref="CharacterQuest.ProgressJson"/>,
 /// written by the zone's QuestProgressStore at each kill tick) so it survives relog and goes back out in the
 /// login 2310 quest vector. Only the in-progress document is touched: an accept starts a fresh one, a give-up
 /// deletes it and a completion closes it, so the progress resets with the quest's run.</summary>
    Task SaveProgressAsync(uint characterId, uint questId, string? progressJson);
}
