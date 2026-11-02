namespace RO2.Monster.Domain;

/// <summary>A player as the aggro scan sees it: horizontal position and level.</summary>
public readonly record struct AggroCandidate(float X, float Y, int Level);

/// <summary>
/// The pure aggro-acquisition rules: whether an aggression mode takes a player, and which candidate in sight is the
/// nearest. No map or death filtering here; the caller hands over the players it already filtered.
/// </summary>
public static class AggroAcquisition
{
    /// <summary>Sight radius for an aggressive monster whose NPCInfo row carries no Reaction_Bound: the modal hostile
    /// Reaction_Bound. It is the radius the server uses for acquisition.</summary>
    public const float DefaultSightRadius = 9f;

    /// <summary>
    /// The nearest candidate the monster at (<paramref name="npcX"/>, <paramref name="npcY"/>) may acquire: inside
    /// its sight radius (<see cref="MonsterAiState.SightRadius"/>, else <see cref="DefaultSightRadius"/>; boundary
    /// inclusive) and allowed by its aggression mode. Nearest by squared 2D distance; a non-finite distance is
    /// rejected; on an equal distance the LAST scanned candidate wins. Null when none qualifies.
    /// </summary>
    public static T? Nearest<T>(MonsterAiState ai, int npcLevel, float npcX, float npcY,
        ReadOnlySpan<T> candidates, Func<T, AggroCandidate> view) where T : class
    {
        float sight = ai.SightRadius > 0f ? ai.SightRadius : DefaultSightRadius;
        float best = sight * sight;
        T? nearest = null;
        foreach (var c in candidates)
        {
            var v = view(c);
            if (!NpcAggressionRules.Acquires(ai.Aggression, ai.AggressionLevelGap, npcLevel, v.Level))
                continue;
            float dx = v.X - npcX, dy = v.Y - npcY;
            float d2 = dx * dx + dy * dy;
            if (float.IsFinite(d2) && d2 <= best)
            {
                best = d2;
                nearest = c;
            }
        }
        return nearest;
    }
}
