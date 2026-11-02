namespace RO2.Monster.Domain;

/// <summary>The custom per-asset aggression table, as the Domain sees it. The host adapts its loaded table to this port.</summary>
public interface INpcAggressionTable
{
    /// <summary>The rule for <paramref name="asset"/>; false when the table has none for it.</summary>
    bool TryGet(uint asset, out NpcAggressionMode mode, out int levelGap);
}

/// <summary>Pure aggression rules: which camps may aggro, and whether a monster acquires a given player.</summary>
public static class NpcAggressionRules
{
    /// <summary>Highest camp that is a friendly NPC; only a monster (camp above this) ever aggros.</summary>
    public const int MaxFriendlyCamp = 1;

    /// <summary>
    /// Whether a monster of <paramref name="mode"/> acquires a player of <paramref name="playerLevel"/> who entered
    /// its sight: <see cref="NpcAggressionMode.Always"/> always, <see cref="NpcAggressionMode.ByLevel"/> while the
    /// player is at most <paramref name="levelGap"/> levels above the monster, <see cref="NpcAggressionMode.None"/> never.
    /// </summary>
    public static bool Acquires(NpcAggressionMode mode, int levelGap, int npcLevel, int playerLevel) => mode switch
    {
        NpcAggressionMode.Always => true,
        NpcAggressionMode.ByLevel => playerLevel - npcLevel <= levelGap,
        _ => false,
    };

    /// <summary>
    /// The (mode, level gap) a spawn of <paramref name="asset"/> takes: the table's rule, but only for a monster
    /// (<paramref name="camp"/> above <see cref="MaxFriendlyCamp"/>); a friendly NPC, an asset without a rule or a
    /// missing table leave it passive (<see cref="NpcAggressionMode.None"/>, gap 0).
    /// </summary>
    public static (NpcAggressionMode Mode, int LevelGap) Resolve(INpcAggressionTable? table, int camp, uint asset) =>
        camp > MaxFriendlyCamp && table is not null && table.TryGet(asset, out var mode, out var gap)
            ? (mode, gap) : (NpcAggressionMode.None, 0);
}
