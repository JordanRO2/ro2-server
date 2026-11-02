namespace RO2.Monster.Domain;

/// <summary>Whether, and on what condition, a monster starts a fight with a player who has not hit it.</summary>
public enum NpcAggressionMode
{
    /// <summary>Passive: never aggros by proximity, only retaliates when struck (the default for every asset).</summary>
    None = 0,

    /// <summary>Aggros any player that enters its sight radius, regardless of level.</summary>
    Always = 1,

    /// <summary>Aggros a player in its sight radius only while the player is not too far above it in level
    /// (<c>playerLevel - npcLevel &lt;= LevelGap</c>): the classic "stops noticing you once you out-level it" rule.</summary>
    ByLevel = 2,
}

/// <summary>
/// A monster's AI configuration (aggression mode, sight radius, leash) and its post-reset return window. An immutable
/// value; the host stores the next value. The configuration is set once when the spawn is tracked; only
/// <see cref="ReturningUntil"/> changes afterwards (the leash reset stamps it).
/// </summary>
public sealed record MonsterAiState
{
    /// <summary>How this monster starts fights on its own: <see cref="NpcAggressionMode.None"/> (passive, the
    /// default) unless the custom aggression table gives its asset a mode. Friendly NPCs (Camp &lt;= 1) are always None.</summary>
    public NpcAggressionMode Aggression { get; init; }

    /// <summary>The level gap for a <see cref="NpcAggressionMode.ByLevel"/> monster: it acquires a player while
    /// <c>playerLevel - Level &lt;= AggressionLevelGap</c>.</summary>
    public int AggressionLevelGap { get; init; }

    /// <summary>Horizontal radius this monster acquires a player within (NPCInfo.Reaction_Bound). 0 means the row
    /// carried none, and the acquisition scan then uses its melee fallback.</summary>
    public float SightRadius { get; init; }

    /// <summary>How far from its spawn this monster chases before it leashes and resets (NPCInfo.Trace_Bound). 0
    /// means the row carried none, so distance never leashes it (it still resets when it has no live target).</summary>
    public float LeashBound { get; init; }

    /// <summary>While the clock is before this, the monster has just reset and is running home: it is INVULNERABLE
    /// (hits on it miss and bank no threat). Stamped only by the leash reset (now + 3 s). Default (0) = not returning.</summary>
    public DateTime ReturningUntil { get; init; }

    /// <summary>True while the monster is in its post-reset return/invulnerable window.</summary>
    public bool IsReturning(DateTime now) => now < ReturningUntil;
}
