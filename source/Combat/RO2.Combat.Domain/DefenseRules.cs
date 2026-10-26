namespace RO2.Combats.Domain;

/// <summary>Pure AoV armour mitigation: penetration-reduced defence and the DefenseRate damage cut.</summary>
public static class DefenseRules
{
    /// <summary>
    /// AoV DefenseRate denominator add term: Defense% = 100·Def/(Def + <see cref="DefenseRateAdd"/> +
    /// AttackerLv·<see cref="DefenseRateMulti"/>), capped 75%. Backed by PCBaseStatus DefenseAdd (870), uniform
    /// across all class + monster rows, so there is one global pair.
    /// </summary>
    public const int DefenseRateAdd = 870;

    /// <summary>AoV DefenseRate denominator per-level term (PCBaseStatus DefenseMulti 35), uniform across all rows.</summary>
    public const int DefenseRateMulti = 35;

    /// <summary>
    /// The defender's effective armour after the attacker's Penetration: <c>Def·(1 − PenPct/100)</c>,
    /// <c>PenPct = min(50, Pen/(0.005·Pen + 0.2·max(1,AttackerLv) + 10))</c> (already a percentage, no extra ×100;
    /// 50% ceiling = PenLimitValue 0.5). Pen 0 or Def 0 returns <c>Math.Max(0, def)</c> as a double — a strict
    /// no-op, so behaviour is byte-identical until Pen gear is worn.
    /// </summary>
    public static double PenetratedDef(int def, int pen, int attackerLevel)
    {
        if (pen <= 0 || def <= 0) return Math.Max(0, def);
        double penPct = Math.Min(50.0, pen / (0.005 * pen + 0.2 * Math.Max(1, attackerLevel) + 10.0));
        return def * (1.0 - penPct / 100.0);
    }

    /// <summary>
    /// The AoV DefenseRate mitigation: Defense% = 100·Def_eff/(Def_eff + 870 + AttackerLv·35), capped 75%, where
    /// Def_eff is the armour left after the attacker's Penetration (<see cref="PenetratedDef"/>, a no-op at Pen 0).
    /// Never below 1. The banker's <c>Math.Round</c> and the max(1) floor are preserved exactly.
    /// </summary>
    public static int Mitigate(int rawDamage, int defense, int attackerPen, int attackerLevel)
    {
        double defEff = PenetratedDef(Math.Max(0, defense), attackerPen, attackerLevel);
        double defPct = Math.Min(75.0, 100.0 * defEff / (defEff + DefenseRateAdd + attackerLevel * DefenseRateMulti));
        return Math.Max(1, (int)Math.Round(rawDamage * (1.0 - defPct / 100.0)));
    }
}
