namespace RO2.Characters.Domain;

/// <summary>Pure rules for the key-config update (RMI 1423) and reset (INIT_KEYCONFIG).</summary>
public static class KeyConfigRules
{
    /// <summary>True when the update only clears the flag: mode 0 with no records. The stored records are kept.</summary>
    public static bool IsFlagOnlyUpdate(ushort mode, int entryBytes) => mode == 0 && entryBytes == 0;

    /// <summary>
    /// The document to store after a stored-set reset: the same character and mode with no bindings, or null when
    /// there is nothing to change (no stored record, or it already has no bindings).
    /// </summary>
    public static KeyConfigDocument? AfterReset(KeyConfigDocument? stored, DateTime updatedUtc)
    {
        if (stored is null || stored.Entries.Length == 0) return null;
        return new KeyConfigDocument
        {
            Id = stored.Id,
            Mode = stored.Mode,
            Entries = Array.Empty<byte>(),
            UpdatedUtc = updatedUtc,
        };
    }
}
