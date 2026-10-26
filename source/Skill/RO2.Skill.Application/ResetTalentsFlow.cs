using RO2.GameData;
using RO2.Skills.Domain;

namespace RO2.Skills.Application;

/// <summary>
/// The persistence + mirror half of a skill reset (a reset scroll or a Talent_Reset job change): drop the learned
/// talent groups that are not starters, put every starter back at level 1, and mirror the reset tree. The host
/// keeps the quick-slot clear (2685), the 2488 list, the 2544 pool, the effect drop and the logging.
/// </summary>
public static class ResetTalentsFlow
{
    /// <summary>The groups dropped and the job's starter set, for the host's logging and follow-up sends.</summary>
    public readonly record struct ResetResult(uint[] Dropped, uint[] Starters);

    /// <summary>
    /// Persists the reset — DELETE the dropped groups, then LEARN each missing starter at 1 / set a present starter
    /// back to 1 — and mirrors it. Stored before the mirror: a throw means nothing is sent and the scroll is not
    /// consumed (no 2329), and the next login re-seeds any missing starter.
    /// </summary>
    /// <param name="session">The live learned-skill tree.</param>
    /// <param name="repo">The skill repository.</param>
    /// <param name="store">Game data, for the starter set and talent groups.</param>
    /// <param name="job">The job to reset to (the current job for a scroll, the new Job_ID for a job change).</param>
    /// <param name="characterId">The character the reset persists for.</param>
    public static async Task<ResetResult> ResetAsync(ILearnedSkillsSession session, ISkillRepository repo,
        GameDataStore store, byte job, uint characterId)
    {
        var starters = TalentRules.StarterSkillGroups(store, job);
        var learned = session.Snapshot();
        var dropped = TalentRules.GroupsDroppedByReset(store, job, learned.Keys);

        if (dropped.Length > 0)
            await repo.DeleteSkillsAsync(characterId, dropped);
        foreach (var group in starters)
        {
            if (!learned.TryGetValue(group, out var level))
                await repo.LearnSkillAsync(characterId, group, 1);
            else if (level != 1)
                await repo.UpdateSkillLevelAsync(characterId, group, 1);
        }
        session.ResetLearnedSkills(dropped, starters);

        return new ResetResult(dropped, starters);
    }
}
