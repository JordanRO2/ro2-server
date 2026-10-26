using RO2.GameData;
using RO2.Skills.Domain;

namespace RO2.Skills.Application;

/// <summary>
/// Spends talent points on a skill allocation (RMI 1310 ReqPCUseTalentPoint): validate the whole request against
/// the authoritative Talent_Table, persist it as ONE batch, and only then mirror it onto the live tree — all under
/// the player's write gate. The wire parse and the 2543 reply stay in the host.
/// </summary>
public static class UseTalentPointFlow
{
    /// <summary>What happened to a 1310 allocation.</summary>
    public enum UseTalentOutcome
    {
        /// <summary>An entry was illegal for the class/level/tree: nothing persisted, no reply.</summary>
        IllegalRefused,

        /// <summary>The batch write threw: nothing mirrored, no reply.</summary>
        WriteFailed,

        /// <summary>Applied (or an accepted empty no-op): the host sends the 2543 with the remaining pool.</summary>
        Applied,
    }

    /// <summary>The outcome, a refusal reason or write error for the host log, and the post-change pool figures.</summary>
    public readonly record struct UseTalentResult(
        UseTalentOutcome Outcome, string? Reason, Exception? WriteError, byte Remaining, int Spent, int Earned);

    /// <summary>
    /// Under the write gate: checks legality (when there is anything to allocate), upserts the allocation in one
    /// batch, mirrors it on success, and computes the pool left from the whole stored tree. An empty allocation is
    /// an accepted no-op that still reports the current pool. Nothing is sent here; the host maps the result.
    /// </summary>
    /// <param name="session">The live learned-skill tree.</param>
    /// <param name="repo">The skill repository.</param>
    /// <param name="store">Game data, for the talent rules.</param>
    /// <param name="job">The character's class.</param>
    /// <param name="level">The character's level.</param>
    /// <param name="characterId">The character the allocation persists for.</param>
    /// <param name="allocations">The deduplicated allocation vector (group → new level).</param>
    /// <param name="ct">A cancellation token.</param>
    public static Task<UseTalentResult> ApplyAsync(ILearnedSkillsSession session, ISkillRepository repo,
        GameDataStore store, byte job, int level, uint characterId,
        IReadOnlyList<(uint Group, byte Level)> allocations, CancellationToken ct = default)
        => session.WithWriteGateAsync(async () =>
        {
            if (allocations.Count > 0
                && !TalentRules.IsTalentAllocationLegal(store, job, level, allocations, session.Snapshot(), out var reason))
                return new UseTalentResult(UseTalentOutcome.IllegalRefused, reason, null, 0, 0, 0);

            if (allocations.Count > 0)
            {
                // ONE batched upsert; the mirror is applied only after it returns, so a failed write leaves the tree
                // as it was and no reply goes out.
                try
                {
                    await repo.UpsertSkillsAsync(characterId, allocations, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return new UseTalentResult(UseTalentOutcome.WriteFailed, null, ex, 0, 0, 0);
                }
                session.Overlay(allocations);
            }

            // The 2543 pool is the whole stored tree's remainder (level - 1 less the Talent_Table levels held,
            // starters' first level free), computed AFTER the overlay, as the relog 2310 and the reset 2544 do.
            var tree = session.Snapshot().Select(kv => (kv.Key, kv.Value)).ToArray();
            byte remaining = TalentRules.TalentPointsRemaining(store, job, level, tree);
            int spent = TalentRules.TalentPointsSpent(store, job, tree);
            int earned = Math.Max(0, level - 1);
            return new UseTalentResult(UseTalentOutcome.Applied, null, null, remaining, spent, earned);
        }, ct);
}
