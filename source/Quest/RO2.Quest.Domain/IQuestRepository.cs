namespace RO2.Quests.Domain;

/// <summary>Narrow read port over a character's quest completions, for other contexts that only gate on them.</summary>
public interface IQuestCompletionView
{
    Task<IReadOnlyList<uint>> GetCompletedQuestIdsAsync(uint characterId);

    /// <summary>When the character last completed <paramref name="questId"/> (UTC), or null if it never has.</summary>
    Task<DateTime?> GetLastCompletedAtAsync(uint characterId, uint questId);

    /// <summary>Every completed quest with the UTC time it was completed, for the login 2310 repeatable-quest react
    /// list. Defaults to empty so test doubles need not implement it; the real repositories override it.</summary>
    Task<IReadOnlyList<(uint QuestId, DateTime CompletedUtc)>> GetCompletionsAsync(uint characterId)
        => Task.FromResult<IReadOnlyList<(uint QuestId, DateTime CompletedUtc)>>([]);
}

public interface IQuestRepository : IQuestCompletionView
{
    Task<IReadOnlyList<CharacterQuest>> GetActiveQuestsAsync(uint characterId);
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
