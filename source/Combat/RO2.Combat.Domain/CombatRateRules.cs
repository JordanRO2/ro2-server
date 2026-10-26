namespace RO2.Combats.Domain;

/// <summary>Pure AoV hit-outcome chances (percent): crit and parry, from the attacker/defender level gap. The RNG roll stays host-side.</summary>
public static class CombatRateRules
{
    /// <summary>Crit chance in percent: <c>Crit/(0.02·Crit + 40 + 0.8·(DefLv−AtkLv))</c>, capped 90% (AoV); 0 without Crit.</summary>
    public static double CritRatePct(int crit, int defenderLevel, int attackerLevel)
        => crit <= 0 ? 0 : Math.Min(90.0, crit / (0.02 * crit + 40.0 + 0.8 * (defenderLevel - attackerLevel)));

    /// <summary>Parry chance in percent: <c>Parry/(0.02·Parry + 40 + 0.8·(DefLv−AtkLv))</c>, capped 70% (AoV); 0 without Parry.</summary>
    public static double ParryRatePct(int parry, int defenderLevel, int attackerLevel)
        => parry <= 0 ? 0 : Math.Min(70.0, parry / (0.02 * parry + 40.0 + 0.8 * (defenderLevel - attackerLevel)));
}
