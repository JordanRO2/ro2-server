using RO2.GameData;

namespace RO2.Characters.Application;

/// <summary>
/// Read-only access to the static game-data tables a character flow needs, so the flows do not depend on the
/// host's data-store type. The host backs it with its loaded tables. The surface grows one slice at a time.
/// </summary>
public interface ICharacterGameData
{
    /// <summary>True when <paramref name="unionId"/> is a union defined in Union_Info.</summary>
    bool UnionExists(uint unionId);

    /// <summary>The job (profession) experience table, keyed by level; its Base_Exp_Next column holds the thresholds.</summary>
    IReadOnlyDictionary<byte, ExpTableEntry> ExpTable { get; }

    /// <summary>The class's starting primary stats (STR, AGI, INT, WIS, VIT, LUK) from Standard_Stat, or null when it has no row.</summary>
    (int str, int agi, int intel, int wis, int vit, int luk)? ClassBasePrimaries(byte job);

    /// <summary>The points one more allocation costs for a stat currently at <paramref name="currentStatValue"/>; 0 = cannot be raised.</summary>
    int BonusStatCost(int currentStatValue);
}
