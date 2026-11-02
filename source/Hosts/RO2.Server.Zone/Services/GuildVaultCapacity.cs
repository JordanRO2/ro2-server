using RO2.GameData.Tables;
using RO2.Guilds.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>Host forwarding shim over <see cref="GuildVaultRules"/> (the vault capacity rules live in RO2.Guild.Domain).</summary>
internal static class GuildVaultCapacity
{
    internal const byte SkillCategoryVaultCells = GuildVaultRules.SkillCategoryVaultCells;
    internal const ushort ConfigMaxVaultCells = GuildVaultRules.ConfigMaxVaultCells;
    internal const int AddressableCells = GuildVaultRules.AddressableCells;

    internal static int SlotCapacity(IEnumerable<uint> learned, IReadOnlyDictionary<uint, GuildSkillLevel> table,
        IReadOnlyList<ConfigDef> config) => GuildVaultRules.SlotCapacity(learned, table, config);

    internal static int MaxCells(IReadOnlyList<ConfigDef> config) => GuildVaultRules.MaxCells(config);

    internal static int LowestFreeCell(IEnumerable<GuildStorageItem> storage, int capacity)
        => GuildVaultRules.LowestFreeCell(storage, capacity);
}
