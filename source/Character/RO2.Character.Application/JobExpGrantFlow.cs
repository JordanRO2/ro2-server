using RO2.Characters.Domain;

namespace RO2.Characters.Application;

/// <summary>
/// Grants job (profession) experience: applies the gain to the character, notifies the client and world, and
/// persists the new level/exp pair. The pure math lives in <see cref="JobExpRules"/>; this owns the order of
/// effects, which must stay exactly as the handler had it.
/// </summary>
public static class JobExpGrantFlow
{
    /// <summary>
    /// Adds <paramref name="gain"/> to the character's job exp. Returns false with nothing changed or sent when
    /// there is no gain, no character, or it is at the cap or on a level with no table row. Otherwise it mutates
    /// the in-memory pair, persists only the JobLevel/JobExp pair, announces it, and on a level-up runs
    /// the Khara follow-up last. A persist failure is swallowed and still returns true.
    /// </summary>
    /// <param name="character">The player's in-memory character, mutated in place.</param>
    /// <param name="gain">The job exp to add.</param>
    /// <param name="data">Game data, for the exp table.</param>
    /// <param name="characters">The character repository, for the persist.</param>
    /// <param name="notifier">The host's notifications and follow-ups.</param>
    /// <param name="ct">A cancellation token (the repository calls take none, by design).</param>
    public static async Task<bool> GrantAsync(Character? character, ulong gain, ICharacterGameData data,
        ICharacterRepository characters, IJobExpNotifier notifier, CancellationToken ct = default)
    {
        if (gain == 0 || character is null) return false;
        var table = data.ExpTable;
        // At the job-level cap the exp would be dropped; a started Master level takes it as mastery points instead.
        if (character.JobLevel >= JobExpRules.MaxLevel(table))
            return await TryEarnMasteryAsync(character, gain, data, characters, notifier);
        if (!table.ContainsKey(character.JobLevel)) return false;

        var result = JobExpRules.Apply(character.JobLevel, character.JobExp, gain, table);
        character.JobLevel = result.Level;
        character.JobExp = result.Exp;

        // Persist before the sends so a dropped session throwing out of the announce cannot lose the pair. A
        // targeted write of only the pair, so fields updated elsewhere survive. A failure is swallowed, so the
        // client stays ahead of the DB rather than losing the level-up.
        try
        {
            await characters.UpdateJobLevelExpAsync(character.Id, result.Level, result.Exp);
        }
        catch (Exception ex)
        {
            notifier.PersistFailed(ex, character.Id, result);
        }

        await notifier.AnnounceAsync(result, gain);

        if (result.LevelledUp)
            await notifier.OnLevelUpAsync();
        return true;
    }

    /// <summary>
    /// Converts a capped job-exp gain to mastery points: once the character has started the Master level, combat
    /// profession exp feeds the point balance one for one, up to the current rank's point cap, and a 2986 change is
    /// sent. Returns false with nothing changed when it has not started or is already at the cap.
    /// </summary>
    private static async Task<bool> TryEarnMasteryAsync(Character character, ulong gain, ICharacterGameData data,
        ICharacterRepository characters, IJobExpNotifier notifier)
    {
        if (character.MasteryLevel < 1) return false;
        ulong cap = data.MasteryMaxPointBalance(character.MasteryLevel);
        if (cap == 0 || character.JobMasteryPoints >= cap) return false;

        ulong added = Math.Min(gain, cap - character.JobMasteryPoints);
        character.JobMasteryPoints += added;
        try
        {
            await characters.UpdateJobMasteryAsync(character.Id, character.MasteryLevel, character.MasteryRank, character.JobMasteryPoints);
        }
        catch
        {
            // Swallowed as the job-exp persist is: the client is left ahead of the DB rather than losing the gain.
        }
        await notifier.MasteryPointsGainedAsync((long)added, character.JobMasteryPoints);
        return true;
    }
}
