using RO2.Characters.Domain;

namespace RO2.Characters.Application;

/// <summary>Key-config use-case flows over <see cref="IKeyConfigRepository"/>. The host parses the wire, picks the
/// log text from the outcome, and sends no reply (both requests are fire-and-forget).</summary>
public static class KeyConfigFlows
{
    /// <summary>Outcome of a stored-set reset.</summary>
    public enum ResetOutcome
    {
        /// <summary>There was no stored record (or it had no bindings) to clear; nothing was written.</summary>
        NothingToClear,
        /// <summary>The stored bindings were cleared (the stored mode is kept).</summary>
        Cleared,
    }

    /// <summary>Clears a character's stored key bindings (INIT_KEYCONFIG, mode != 0). Non-atomic get-then-save,
    /// as before. Writes nothing when there is nothing stored to clear.</summary>
    public static async Task<ResetOutcome> ResetAsync(IKeyConfigRepository repo, uint characterId, DateTime nowUtc, CancellationToken ct = default)
    {
        var reset = KeyConfigRules.AfterReset(await repo.GetAsync(characterId, ct), nowUtc);
        if (reset is null) return ResetOutcome.NothingToClear;
        await repo.SaveAsync(reset, ct);
        return ResetOutcome.Cleared;
    }

    /// <summary>Applies a 1423 key-config update (one atomic repo write). Returns the binding count and whether it
    /// was a flag-only update (the stored list is kept).</summary>
    public static async Task<(int Count, bool FlagOnly)> UpdateAsync(IKeyConfigRepository repo, uint characterId,
        ushort mode, byte[] entries, DateTime nowUtc, CancellationToken ct = default)
    {
        await repo.ApplyUpdateAsync(characterId, mode, entries, nowUtc, ct);
        return (entries.Length / 4, KeyConfigRules.IsFlagOnlyUpdate(mode, entries.Length));
    }
}
