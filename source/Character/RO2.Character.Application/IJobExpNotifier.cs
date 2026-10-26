using RO2.Characters.Domain;

namespace RO2.Characters.Application;

/// <summary>
/// The host side of a job-exp grant: the client/world notifications, the persist-failure log, and the level-up
/// follow-up. The flow drives these in a fixed order so the wire stays byte-identical to the handler it replaced.
/// </summary>
public interface IJobExpNotifier
{
    /// <summary>
    /// Sends 2526 OnNotifyProJobExpChange, and on a level-up also 2527 to the player, 2527 to nearby players, and
    /// the level-up log. Runs after the in-memory mutation and before the persist.
    /// </summary>
    Task AnnounceAsync(JobExpRules.Result result, ulong gain);

    /// <summary>Logs a swallowed persistence failure; the grant still succeeds (the client is left ahead of the DB).</summary>
    void PersistFailed(Exception ex, uint characterId, JobExpRules.Result result);

    /// <summary>Re-checks the job-level Khara quests. Runs last, after the persist, only on a level-up.</summary>
    Task OnLevelUpAsync();
}
