using RO2.GameData.Tables;

namespace RO2.Characters.Domain;

/// <summary>Pure character stat rules over primitives and game-data rows (no GameDataStore): the percent-trait
/// divisor, the bonus-point curve, class/level base stats, class starting primaries, a bonus-stat reset, and the
/// per-stat allocation cost. The host resolves the rows from the store and calls these.</summary>
public static class CharacterStats
{
    /// <summary>
    /// Divisor that turns a <c>Trait_Kind == 2</c> <c>Value</c> into percentage points (<c>Value / 100</c>),
    /// so <c>Value 1000 = 10</c> percentage points = ×1.10.
    /// </summary>
    public const float PercentTraitDivisor = 100f;

    /// <summary>
    /// Total bonus stat points a character has EARNED by reaching <paramref name="level"/> (cumulative,
    /// 0 at level 1): 1 point per level up to 24, 2 per level above 24. Unspent points = this minus the sum
    /// of the character's allocated bonuses.
    /// </summary>
    public static int BonusStatPointsEarned(int level)
    {
        int lvl = Math.Max(1, level);
        int total = 0;
        for (int l = 2; l <= lvl; l++)
            total += l <= 24 ? 1 : 2;
        return total;
    }

    /// <summary>
    /// A character's BASE (naked, no-gear) stats from its class <paramref name="pc"/> row, level and primaries,
    /// via the RO2 formula reconstructed from PCBaseStatus. <see cref="BaseStats.MaxHp"/> is 0 and the derived map
    /// empty when <paramref name="pc"/> is null (the class has no row); the caller then keeps its own fallback.
    /// </summary>
    public static BaseStats ComputeBaseStats(PCBaseStatus? pc, int level, int str, int agi, int intel, int wis, int vit, int luk)
    {
        if (pc is null)
            return new BaseStats(0, 0, new Dictionary<byte, int>());

        int lvl = Math.Max(1, level);
        long basePool = (long)Math.Floor((double)pc.BaseHP * pc.BaseHPRate * pc.BaseHPRateValue);
        long maxHp = basePool + (long)pc.BaseHPValue * (lvl - 1) + (long)pc.VitHPValue * vit;
        long maxSp = (long)pc.WisSPValue * wis;

        var d = new Dictionary<byte, int>
        {
            [23] = pc.APValue * str,                                   // Physical Attack
            [25] = pc.MAValue * intel,                                 // Magic Attack
            [26] = pc.VitDefenseValue * vit,                           // Defense (naked)   (+armor elsewhere)
            [60] = pc.AgiCrticalValue * agi,                           // Critical
            [61] = pc.StrParryValue * str + pc.IntParryValue * intel,  // Parry / Guard
            [59] = pc.AgiDodgeValue * agi,                             // Dodge / Evade     (base only; +1 level term omitted)
            [58] = pc.StrHitValue * str + pc.IntHitValue * intel,      // Hit / Precision   (partial; level term unresolved)
        };
        return new BaseStats(
            (uint)Math.Clamp(maxHp, 0, uint.MaxValue),
            (ushort)Math.Clamp(maxSp, 0, ushort.MaxValue),
            d);
    }

    /// <summary>
    /// A new character's starting primary stats for its class, from its <paramref name="s"/> Standard_Stat row:
    /// STR=Base_STR, AGI=Base_AGI, INT=Base_INT, VIT=Base_VIT, LUK=Base_LUK, and WIS=Base_DEX (the legacy Dex
    /// column). Null when <paramref name="s"/> is null (the class has no row; the caller keeps its fallback).
    /// </summary>
    public static (int str, int agi, int intel, int wis, int vit, int luk)? ClassBasePrimaries(StandardStat? s)
    {
        if (s is null) return null;
        return (s.Base_STR, s.Base_AGI, s.Base_INT, s.Base_DEX, s.Base_VIT, s.Base_LUK);
    }

    /// <summary>
    /// Refunds all allocated bonus stat points: zeroes the per-stat allocation deltas and resets the effective
    /// primaries to the pure class base <paramref name="primaries"/>. Returns false and changes nothing when
    /// <paramref name="primaries"/> is null (the class has no Standard_Stat row). The caller mirrors the primaries
    /// to the runtime PlayerState, persists, and re-pushes the 2498 sheet.
    /// </summary>
    public static bool ResetBonusStats(Character c, (int str, int agi, int intel, int wis, int vit, int luk)? primaries)
    {
        if (primaries is not { } cb) return false;
        c.BonusStr = c.BonusAgi = c.BonusInt = c.BonusWis = c.BonusVit = c.BonusLuk = 0;
        c.Str = cb.str; c.Agi = cb.agi; c.Int = cb.intel;
        c.Wis = cb.wis; c.Vit = cb.vit; c.Luk = cb.luk;
        return true;
    }

    /// <summary>
    /// Points required to raise a primary from <paramref name="currentStatValue"/> by one, from the
    /// <c>Curr_Stat → Require_Point</c> map (Status_Point.json). Defaults to 1 when the value is off the table.
    /// </summary>
    public static int BonusStatCost(IReadOnlyDictionary<int, byte> costByCurrentStat, int currentStatValue)
        => costByCurrentStat.TryGetValue(currentStatValue, out var c) ? c : 1;
}
