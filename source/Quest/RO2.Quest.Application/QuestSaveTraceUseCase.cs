using RO2.Quests.Domain;

namespace RO2.Quests.Application;

/// <summary>
/// The 1540 save: keeps only the requested quests the character still holds (<see cref="TraceRules.Normalize"/>) and
/// persists that set. Returns it so the host can mirror it into the live session.
/// </summary>
public sealed class QuestSaveTraceUseCase(IQuestRepository quests)
{
    /// <summary>Normalizes against the held quests, persists, and returns the traced set.</summary>
    public async Task<HashSet<uint>> SaveAsync(uint characterId, IEnumerable<uint> requested, ISet<uint> active)
    {
        var traced = TraceRules.Normalize(requested, active);
        await quests.SetQuestsTracedAsync(characterId, traced);
        return traced;
    }
}
