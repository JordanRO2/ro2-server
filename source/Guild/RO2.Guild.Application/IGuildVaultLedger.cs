using RO2.Guilds.Domain;

namespace RO2.Guilds.Application;

/// <summary>The result of a vault room check or store.</summary>
public enum VaultStoreOutcome
{
    /// <summary>There is room (check) or the item was added to the vault and the guild written (store).</summary>
    Stored,

    /// <summary>No vault cell below the capacity was free.</summary>
    Full,

    /// <summary>The guild was disbanded before the call.</summary>
    NotRegistered,
}

/// <summary>
/// The guild-side steps of a vault item move: each takes the guild's gate and applies the vault change in memory,
/// and writes the guild where it says so. The live registry implements it in the host.
/// </summary>
public interface IGuildVaultLedger
{
    /// <summary>Whether the guild is still registered and a vault cell below <paramref name="capacity"/> is free; <see cref="VaultStoreOutcome.Stored"/> means there is room.</summary>
    VaultStoreOutcome CheckRoom(Guild guild, int capacity);

    /// <summary>Puts <paramref name="rec"/> in the lowest free cell (setting its StorageSlot) and writes the guild; nothing is added or written when full or disbanded.</summary>
    Task<VaultStoreOutcome> StoreAsync(Guild guild, GuildStorageItem rec, int capacity);

    /// <summary>Drops the vault record with this uid, if present.</summary>
    void Remove(Guild guild, ulong uid);

    /// <summary>Removes and returns the record in this vault cell, or null when it is empty.</summary>
    GuildStorageItem? Take(Guild guild, byte storageSlot);

    /// <summary>Puts a taken record back in the vault, in memory only.</summary>
    void PutBack(Guild guild, GuildStorageItem rec);

    /// <summary>Writes the guild (a failed or skipped write is swallowed, as the registry does).</summary>
    Task PersistAsync(Guild guild);
}
