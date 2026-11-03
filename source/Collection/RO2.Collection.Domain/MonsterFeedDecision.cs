using RO2.GameData.Tables;

namespace RO2.Collections.Domain;

/// <summary>What feeding one DNA to a monster collection does.</summary>
public enum MonsterFeedKind
{
    /// <summary>The first DNA: the collection is registered at level 1, exp 0.</summary>
    Registered,
    /// <summary>The collection was already at its highest level: nothing changes and no DNA is taken.</summary>
    MaxLevel,
    /// <summary>One exp was added, and the collection levelled up when it reached the level exp.</summary>
    ExpGained,
}

/// <summary>The outcome of a DNA feed: what happened, the resulting level and exp, and the 2923 result code.</summary>
public readonly record struct MonsterFeedOutcome(MonsterFeedKind Kind, byte Level, uint Exp, ushort Result);

/// <summary>
/// The pure decision of RMI 1623 (feed one monster DNA): register, refuse at the top level, or add one exp and level up.
/// The CollectMonster rows come in as an argument; the bag, the stored progress, the count header and the wire stay in the Zone host.
/// </summary>
public static class MonsterFeedDecision
{
    /// <summary>2923 result when the collection is already at its highest level.</summary>
    public const ushort ResultMaxLevel = 30005;

    /// <summary>2923 result when a monster DNA registers a new collection.</summary>
    public const ushort ResultRegistered = 30008;

    /// <summary>2923 result when a DNA adds experience to a registered collection.</summary>
    public const ushort ResultExpGained = 30009;

    /// <summary>
    /// Decides one DNA feed for <paramref name="npcId"/>, given whether it is registered and its stored level and exp (1 and 0 when it has none).
    /// An unregistered collection registers whatever the stored values say. A registered one at or above its highest level is refused with its
    /// values unchanged.
    /// </summary>
    public static MonsterFeedOutcome Decide(IEnumerable<CollectMonster> rows, uint npcId, bool registered, byte level, uint exp)
    {
        if (!registered)
            return new(MonsterFeedKind.Registered, 1, 0, ResultRegistered);
        if (level >= CollectionRules.MaxLevel(rows, npcId))
            return new(MonsterFeedKind.MaxLevel, level, exp, ResultMaxLevel);
        exp++;
        if (CollectionRules.Row(rows, npcId, level) is { } row && exp >= row.Exp)
        {
            level++;
            exp = 0;
        }
        return new(MonsterFeedKind.ExpGained, level, exp, ResultExpGained);
    }
}
