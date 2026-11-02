namespace RO2.Monster.Domain;

/// <summary>The per-(asset, level) NPC vitals table, as the Domain sees it. The host adapts its loaded table to this port.</summary>
public interface IMonsterVitalsCatalog
{
    /// <summary>Max HP and max SP for <paramref name="asset"/> at <paramref name="level"/>, when the table has an entry.</summary>
    bool TryGet(uint asset, int level, out int maxHp, out int maxSp);

    /// <summary>The most common max SP for NPCs of <paramref name="level"/>, a fallback for an asset without its own entry.</summary>
    bool TryGetLevelMaxSp(int level, out int maxSp);
}

/// <summary>Pure rule resolving the (max HP, max SP) an NPC spawns with.</summary>
public static class MonsterVitalsRules
{
    /// <summary>
    /// The catalog's pair when it has (asset, level). Otherwise HP stays <paramref name="tableHp"/> and SP takes
    /// the catalog's typical max SP for the level (when positive), else <paramref name="tableHp"/> clamped to 16 bits.
    /// A null catalog takes the last fallback.
    /// </summary>
    public static (int MaxHp, int MaxSp) Resolve(IMonsterVitalsCatalog? catalog, uint asset, int level, int tableHp)
    {
        if (catalog is not null && catalog.TryGet(asset, level, out var hp, out var sp))
            return (hp, sp);
        if (catalog is not null && catalog.TryGetLevelMaxSp(level, out var levelSp) && levelSp > 0)
            return (tableHp, levelSp);
        return (tableHp, Math.Clamp(tableHp, 0, ushort.MaxValue));
    }
}
