using System.Collections.Concurrent;

namespace RO2.Quests.Domain;

/// <summary>
/// The quests one player has failed this login (Fail_Type time limit / death / kill count). Thread-safe; the owner
/// keeps the instance, this holds only the set logic.
/// </summary>
public sealed class QuestFailedSet
{
    private readonly ConcurrentDictionary<uint, byte> _failed = new();

    public void MarkFailed(uint questId) => _failed.TryAdd(questId, 0);

    public bool IsFailed(uint questId) => _failed.ContainsKey(questId);

    public void Clear(uint questId) => _failed.TryRemove(questId, out _);
}
