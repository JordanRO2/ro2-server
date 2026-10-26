namespace RO2.Combats.Domain;

/// <summary>Pure AoV base-damage (PSE) math. The host resolves the skill id, the per-level DamageRate and the stats.</summary>
public static class DamageFormulas
{
    /// <summary>
    /// The base damage before mitigation: <c>Attack = max(1, max(APValue·STR, MAValue·INT))</c>, then
    /// <c>PSE = Attack·0.095 + (WeaponMin+WeaponMax)/2</c> when a weapon is worn, else the raw Attack; the result is
    /// <c>max(1, round(PSE · skillMod))</c>. When the class has no PCBaseStatus row, the physical term falls back to
    /// <c>STR·2</c> (UNVERIFIED placeholder, preserved as-is) and the magic term is 0.
    /// </summary>
    public static int BaseDamage(bool hasPcRow, int apValue, int maValue, int str, int intel,
        int weaponMin, int weaponMax, double skillMod)
    {
        int physAtk = hasPcRow ? apValue * str : str * 2;
        int magAtk = hasPcRow ? maValue * intel : 0;
        int attack = Math.Max(1, Math.Max(physAtk, magAtk));

        double baseEffect = (weaponMin > 0 || weaponMax > 0)
            ? attack * 0.095 + (weaponMin + weaponMax) / 2.0
            : attack;

        return Math.Max(1, (int)Math.Round(baseEffect * skillMod));
    }
}
