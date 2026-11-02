namespace RO2.Combats.Domain;

/// <summary>
/// Pure damage resolution of a monster's swing at a player. NPCs carry no Crit or Pen stat, so there is no crit and
/// the penetration is none. The caller resolves the live-effect input (a Miss_Rate at or above full is
/// <c>evaded</c>) and owns the swing cadence and the HP write; the Domain owns the RNG only through the roller.
/// </summary>
public static class MonsterSwingDamage
{
    /// <summary>Damage multiplier of a guarded (parried) hit.</summary>
    public const double GuardDamageMultiplier = 0.5;

    /// <summary>Lowest damage a landed monster hit deals.</summary>
    public const int MinHitDamage = 1;

    /// <summary>
    /// The outcome of one swing: the Atk cut by the victim's Defense (<see cref="DefenseRules.Mitigate"/>, 75% cap,
    /// attacker level clamped at 0), then <see cref="HitKind.Miss"/> (damage 0, no roll) when <paramref name="evaded"/>,
    /// else the victim's parry (<see cref="CombatRateRules.ParryRatePct"/>, 70% cap, rolled only when Parry &gt; 0)
    /// guards it to ×0.5 rounded and floored at 1, else <see cref="HitKind.Normal"/>.
    /// </summary>
    public static (int Damage, HitKind Kind) Resolve(int monsterAtk, int monsterLevel, int victimDefense,
        int victimParry, int victimLevel, bool evaded, IRoller roller)
    {
        int level = Math.Max(0, monsterLevel);
        int damage = DefenseRules.Mitigate(monsterAtk, victimDefense, 0, level);
        if (evaded)
            return (0, HitKind.Miss);
        if (victimParry > 0 && roller.Roll(CombatRateRules.ParryRatePct(victimParry, victimLevel, level)))
            return (Math.Max(MinHitDamage, (int)Math.Round(damage * GuardDamageMultiplier)), HitKind.Guard);
        return (damage, HitKind.Normal);
    }
}
