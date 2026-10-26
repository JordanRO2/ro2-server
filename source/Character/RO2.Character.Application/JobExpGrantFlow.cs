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
    /// the in-memory pair, announces it, persists a fresh row (set JobLevel/JobExp only), and on a level-up runs
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
        if (character.JobLevel >= JobExpRules.MaxLevel(table) || !table.ContainsKey(character.JobLevel)) return false;

        var result = JobExpRules.Apply(character.JobLevel, character.JobExp, gain, table);
        character.JobLevel = result.Level;
        character.JobExp = result.Exp;

        await notifier.AnnounceAsync(result, gain);

        // Persist after the sends: read a fresh row and set only the pair, so fields updated elsewhere survive. A
        // failure is swallowed, so the client stays ahead of the DB rather than losing the level-up.
        try
        {
            var row = await characters.GetByIdAsync(character.Id);
            if (row is not null)
            {
                row.JobLevel = result.Level;
                row.JobExp = result.Exp;
                await characters.SaveCharacterAsync(row);
            }
        }
        catch (Exception ex)
        {
            notifier.PersistFailed(ex, character.Id, result);
        }

        if (result.LevelledUp)
            await notifier.OnLevelUpAsync();
        return true;
    }
}
