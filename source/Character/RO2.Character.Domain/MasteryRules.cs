using RO2.GameData;

namespace RO2.Characters.Domain;

/// <summary>
/// Pure rules for the Master level (job mastery) progression: who may start it, the per-rank level cap, and the
/// rank a level sits in. The mastery point/coin costs come from the data tables and are applied by the host; this
/// holds only the table-free invariants.
/// </summary>
public static class MasteryRules
{
    /// <summary>The base level a character must reach before it can convert to Master level 1.</summary>
    public const byte BaseLevelToStart = 50;

    /// <summary>Highest Master level, reached only at the top rank; the practical cap is the rank-gated one below.</summary>
    public const byte MaxMasterLevel = 50;

    /// <summary>
    /// True when the character may convert to Master level 1: it has reached <see cref="BaseLevelToStart"/> and has
    /// not started the Master level yet.
    /// </summary>
    public static bool CanStart(byte baseLevel, byte currentMasteryLevel)
        => baseLevel >= BaseLevelToStart && currentMasteryLevel == 0;

    /// <summary>
    /// The Master-level cap of a rank: 10 at rank 0 (D), 20 at 1 (C), 30 at 2 (B), 40 at 3 (A), 50 at 4 (S).
    /// </summary>
    public static byte LevelCapForRank(byte rank) => (byte)((rank + 1) * 10);

    /// <summary>
    /// The point and coin cost to raise the Master level by one from <paramref name="currentLevel"/>: the next
    /// level's MasteryTable row (e.g. level 30 to 31 spends the level-31 row's Mastery_Point_Next). Null when the
    /// character cannot level up: it has not started, is already at the rank's cap, or no table row exists for the
    /// next level. Affordability (the point balance and coin) is the caller's to check.
    /// </summary>
    public static (uint Points, uint Coin)? LevelUpCost(GameDataStore store, byte currentLevel, byte rank)
    {
        if (currentLevel < 1 || currentLevel >= LevelCapForRank(rank)) return null;
        byte target = (byte)(currentLevel + 1);
        foreach (var row in store.MasteryTable)
            if (row.Mastery_Level == target)
                return (row.Mastery_Point_Next, row.NeedCoin);
        return null;
    }
}
