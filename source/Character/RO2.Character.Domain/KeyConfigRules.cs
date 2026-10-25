namespace RO2.Characters.Domain;

/// <summary>Pure rules for the key-config (RMI 1423) update.</summary>
public static class KeyConfigRules
{
    /// <summary>True when the update only clears the flag: mode 0 with no records. The stored records are kept.</summary>
    public static bool IsFlagOnlyUpdate(ushort mode, int entryBytes) => mode == 0 && entryBytes == 0;
}
