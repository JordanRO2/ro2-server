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
}
