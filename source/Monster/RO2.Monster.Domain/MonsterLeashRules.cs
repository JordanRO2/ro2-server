namespace RO2.Monster.Domain;

/// <summary>What <see cref="MonsterLeashRules.Reset"/> produced: the healed vitals, the AI state stamped with the
/// return window, and whether anything changed (threat/target was held, or HP was below max).</summary>
public readonly record struct LeashResetResult(MonsterVitals Vitals, MonsterAiState Ai, bool Changed);

/// <summary>
/// The leash-reset transition: a monster that is no longer in a fight drops its threat and target, returns to full HP
/// and becomes invulnerable for <see cref="ReturnInvulnDuration"/> while it runs home. Pure of locks, clocks and I/O:
/// the host holds HpLock then ThreatLock around the call and stores the result.
/// </summary>
public static class MonsterLeashRules
{
    /// <summary>How long after a reset a monster stays invulnerable while it runs home (hits miss). Fixed: nothing
    /// extends it while the monster walks home.</summary>
    public static readonly TimeSpan ReturnInvulnDuration = TimeSpan.FromSeconds(3);

    /// <summary>Empties <paramref name="threat"/> in place (the caller holds the threat lock), restores
    /// <paramref name="vitals"/> to max HP and stamps <c>ReturningUntil = nowUtc + ReturnInvulnDuration</c>. Does not
    /// check death or handle identity; the caller does. Queues nothing (no 2630 on a leash reset).</summary>
    public static LeashResetResult Reset(MonsterVitals vitals, ThreatTable threat, MonsterAiState ai, DateTime nowUtc)
    {
        bool hadThreat = threat.Clear();
        bool changed = hadThreat || vitals.Hp < vitals.MaxHp;
        return new LeashResetResult(vitals.RestoreHp(), ai with { ReturningUntil = nowUtc + ReturnInvulnDuration }, changed);
    }
}
