namespace RO2.Combats.Domain;

/// <summary>Pure AoV base-damage (PSE) math. The host resolves the skill id, the per-level DamageRate and the stats.</summary>
public static class DamageFormulas
{
    /// <summary>
    /// The base damage before mitigation: <c>Attack = max(1, max(APValue·STR + atkBonusPhys, MAValue·INT +
    /// atkBonusMag))</c>, then <c>PSE = Attack·0.095 + (WeaponMin+WeaponMax)/2</c> when a weapon is worn, else the raw
    /// Attack; the result is <c>max(1, round(PSE · skillMod))</c>. When the class has no PCBaseStatus row, the
    /// physical term falls back to <c>STR·2</c> (UNVERIFIED placeholder, preserved as-is) and the magic term is 0.
    /// <paramref name="atkBonusPhys"/>/<paramref name="atkBonusMag"/> are the gear's flat Attack Power / Magic Power
    /// (TraitInfo trait 23 / 25), the <c>ATKBonus</c> term of the AoV formula; without them gear +Attack showed on
    /// the 2498 sheet but never raised the damage dealt.
    /// </summary>
    public static int BaseDamage(bool hasPcRow, int apValue, int maValue, int str, int intel,
        int weaponMin, int weaponMax, double skillMod, int atkBonusPhys, int atkBonusMag)
    {
        int physAtk = (hasPcRow ? apValue * str : str * 2) + atkBonusPhys;
        int magAtk = (hasPcRow ? maValue * intel : 0) + atkBonusMag;
        int attack = Math.Max(1, Math.Max(physAtk, magAtk));

        double baseEffect = (weaponMin > 0 || weaponMax > 0)
            ? attack * 0.095 + (weaponMin + weaponMax) / 2.0
            : attack;

        return Math.Max(1, (int)Math.Round(baseEffect * skillMod));
    }
}
