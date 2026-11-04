namespace RO2.Combats.Domain;

/// <summary>Pure AoV hit-outcome chances (percent): crit and parry, from the attacker/defender level gap. The RNG roll stays host-side.</summary>
public static class CombatRateRules
{
    /// <summary>Crit chance in percent: <c>Crit/(0.02·Crit + 40 + 0.8·(DefLv−AtkLv))</c>, capped 90% (AoV); 0 without Crit.</summary>
    public static double CritRatePct(int crit, int defenderLevel, int attackerLevel)
        => crit <= 0 ? 0 : Math.Min(90.0, crit / Math.Max(1.0, 0.02 * crit + 40.0 + 0.8 * (defenderLevel - attackerLevel)));

    /// <summary>Parry chance in percent: <c>Parry/(0.02·Parry + 40 + 0.8·(DefLv−AtkLv))</c>, capped 70% (AoV); 0 without Parry.</summary>
    public static double ParryRatePct(int parry, int defenderLevel, int attackerLevel)
        => parry <= 0 ? 0 : Math.Min(70.0, parry / Math.Max(1.0, 0.02 * parry + 40.0 + 0.8 * (defenderLevel - attackerLevel)));

    /// <summary>
    /// Scales a resolved PvP hit by the attacker's PvP Damage Increase (trait 71) and the victim's PvP Damage
    /// Reduction (trait 72), both already-converted percentage points, as a FINAL multiplier (corpus-confirmed
    /// placement): <c>damage · (1 + increase/100) · (1 − reduction/100)</c>.
    /// Retail stores PvP damage in a u16 and does NOT cap reduction, so over-100% reduction flips the sign and
    /// wraps to 65,535 (a self-instakill). A diminishing-returns curve on the rate makes over-100% practically
    /// unreachable; this guards the edge anyway (reduction factor clamped at 0, result floored at 1) rather than
    /// reproduce the overflow.
    /// </summary>
    public static int ApplyPvpDamageRate(int damage, double damageIncreasePct, double damageReductionPct)
    {
        double scaled = damage * (1.0 + damageIncreasePct / 100.0) * Math.Max(0.0, 1.0 - damageReductionPct / 100.0);
        return Math.Max(1, (int)Math.Round(scaled, MidpointRounding.AwayFromZero));
    }
}
