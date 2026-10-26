namespace RO2.Combats.Domain;

/// <summary>The outcome kind of a resolved hit, mapped by the host to the wire DamageKind (1 normal, 3 guard, 4 crit).</summary>
public enum HitKind
{
    /// <summary>An ordinary hit (wire kind 1).</summary>
    Normal,

    /// <summary>A parried/guarded hit, ×0.5 (wire kind 3).</summary>
    Guard,

    /// <summary>A critical hit, ×2 (wire kind 4).</summary>
    Crit,
}

/// <summary>
/// Pure damage-kind resolution. PvE and PvP are deliberately SEPARATE: PvE has no parry, and the precedence and
/// rounding differ. The roll order is the contract — crit first, parry only when crit did not land — and the
/// roller is invoked lazily so a skipped roll consumes no RNG. The miss (returning monster) is decided by the host
/// before this is called.
/// </summary>
public static class HitResolution
{
    /// <summary>
    /// PvE: the already-mitigated damage, floored at 1, then a crit (×2, kind Crit). No parry. The crit uses the
    /// attacker's Crit against the defender's level.
    /// </summary>
    public static (int Damage, HitKind Kind) ResolvePveHit(int mitigatedDamage, int attackerCrit,
        int defenderLevel, int attackerLevel, IRoller roller)
    {
        var damage = Math.Max(1, mitigatedDamage);
        if (roller.Roll(CombatRateRules.CritRatePct(attackerCrit, defenderLevel, attackerLevel)))
            return (Math.Max(1, damage * 2), HitKind.Crit);
        return (damage, HitKind.Normal);
    }

    /// <summary>
    /// PvP: mitigate the base damage, then crit (×2, kind Crit); only if crit missed, parry (×0.5 rounded, floored
    /// at 1, kind Guard); otherwise Normal. Crit uses the attacker's Crit vs the victim level, parry the victim's
    /// Parry vs the attacker level.
    /// </summary>
    public static (int Damage, HitKind Kind) ResolvePvpHit(int baseDamage, int victimDefense, int attackerPen,
        int attackerLevel, int victimLevel, int attackerCrit, int victimParry, IRoller roller)
    {
        var damage = DefenseRules.Mitigate(baseDamage, victimDefense, attackerPen, attackerLevel);
        if (roller.Roll(CombatRateRules.CritRatePct(attackerCrit, victimLevel, attackerLevel)))
            return (Math.Max(1, damage * 2), HitKind.Crit);
        if (roller.Roll(CombatRateRules.ParryRatePct(victimParry, victimLevel, attackerLevel)))
            return (Math.Max(1, (int)Math.Round(damage * 0.5)), HitKind.Guard);
        return (damage, HitKind.Normal);
    }
}
