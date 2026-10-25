using System.Globalization;
using RO2.GameData.Tables;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Vault capacity of a guild: how many item cells the guild vault has, and which cell a deposit takes.
/// </summary>
/// <remarks>
/// <para>
/// The number of cells comes from the guild's learned Guild Storage skill line, capped by the server's
/// configured maximum. A guild without the skill has no vault cells.
/// </para>
/// <para>
/// A deposit takes the lowest free cell of the guild's vault. When no cell below the capacity is free, the
/// deposit is refused.
/// </para>
/// </remarks>
internal static class GuildVaultCapacity
{
 /// <summary>Skill_Category of the Guild Storage skill, which sets the number of vault cells.</summary>
    internal const byte SkillCategoryVaultCells = 3;

 /// <summary>Config_Def type of the maximum number of vault cells.</summary>
    internal const ushort ConfigMaxVaultCells = 10;

 /// <summary>
 /// Number of vault cells the wire format can address, since a cell is one byte. Used as the ceiling when the
 /// configured maximum is missing.
 /// </summary>
    internal const int AddressableCells = 256;

 /// <summary>
 /// Number of vault cells the guild has, from its learned skills, capped at <see cref="MaxCells"/>. Zero when
 /// the guild has not learned the Guild Storage skill.
 /// </summary>
    internal static int SlotCapacity(IEnumerable<uint> learned, IReadOnlyDictionary<uint, GuildSkillLevel> table,
        IReadOnlyList<ConfigDef> config)
    {
        long cells = 0;
        foreach (var id in learned)
        {
            if (!table.TryGetValue(id, out var row)) continue;
            if (row.Skill_Category_1 == SkillCategoryVaultCells) cells = Math.Max(cells, row.Skill_Value_1);
            if (row.Skill_Category_2 == SkillCategoryVaultCells) cells = Math.Max(cells, row.Skill_Value_2);
            if (row.Skill_Category_3 == SkillCategoryVaultCells) cells = Math.Max(cells, row.Skill_Value_3);
        }
        return (int)Math.Min(cells, MaxCells(config));
    }

 /// <summary>
 /// Maximum number of vault cells from the configuration, never above <see cref="AddressableCells"/>. Falls
 /// back to that ceiling when the configuration row is absent or unparseable.
 /// </summary>
    internal static int MaxCells(IReadOnlyList<ConfigDef> config)
    {
        var row = config.FirstOrDefault(c => c.type == ConfigMaxVaultCells);
        return row != null && int.TryParse(row.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var max) && max > 0
            ? Math.Min(max, AddressableCells) : AddressableCells;
    }

 /// <summary>
 /// Returns the lowest vault cell below <paramref name="capacity"/> that holds no stored item, or -1 when all
 /// of them are taken. The caller must hold the guild's lock.
 /// </summary>
    internal static int LowestFreeCell(IEnumerable<GuildStorageItem> storage, int capacity)
    {
        var used = new HashSet<int>(storage.Select(s => (int)s.StorageSlot));
        var limit = Math.Min(capacity, AddressableCells);
        for (var cell = 0; cell < limit; cell++)
            if (!used.Contains(cell)) return cell;
        return -1;
    }
}
