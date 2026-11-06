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

    /// <summary>Bonus stat points the Master level grants per level (MasteryTable Bonus_Stat_Point, a uniform 5).</summary>
    public const int BonusStatPointsPerMasteryLevel = 5;

    /// <summary>
    /// Total bonus stat points a character has earned: the base-level pool (<see cref="BonusStatPointsEarned"/>)
    /// plus the Master-level grant (<see cref="BonusStatPointsPerMasteryLevel"/> per master level) plus the points
    /// bought with the Master Level Stat Point item (<paramref name="boughtPoints"/>, up to 100). Unspent points =
    /// this minus the sum of the character's allocations. At level 50 with full mastery and the purchase: 75 + 200 +
    /// 100 = 375.
    /// </summary>
    public static int TotalBonusStatPointsEarned(int level, int masteryLevel, int boughtPoints)
        => BonusStatPointsEarned(level) + Math.Max(0, masteryLevel) * BonusStatPointsPerMasteryLevel + Math.Max(0, boughtPoints);

    /// <summary>
    /// The retail player HP curve at <paramref name="level"/>: the NpcLevelScaling <c>Base_HP</c> column (levels 1..N,
    /// contiguous, ordered). Past the last level it extrapolates linearly from the last two rows; 0 for an empty table.
    /// </summary>
    public static long LevelBaseHp(IReadOnlyList<NpcLevelScaling> table, int level)
    {
        int n = table.Count;
        if (n == 0) return 0;
        if (level <= n || n == 1) return table[Math.Clamp(level, 1, n) - 1].Base_HP;
        int slope = table[n - 1].Base_HP - table[n - 2].Base_HP;
        return table[n - 1].Base_HP + (long)slope * (level - n);
    }

    /// <summary>
    /// A character's BASE (naked, no-gear) stats from its class <paramref name="pc"/> row, level and primaries,
    /// via the RO2 formula reconstructed from PCBaseStatus. <see cref="BaseStats.MaxHp"/> is 0 and the derived map
    /// empty when <paramref name="pc"/> is null (the class has no row); the caller then keeps its own fallback.
    /// </summary>
    public static BaseStats ComputeBaseStats(PCBaseStatus? pc, IReadOnlyList<NpcLevelScaling> levelScaling, int level, int str, int agi, int intel, int wis, int vit, int luk)
    {
        if (pc is null)
            return new BaseStats(0, 0, new Dictionary<byte, int>());

        int lvl = Math.Max(1, level);
        long maxHp = LevelBaseHp(levelScaling, lvl) + (long)pc.VitHPValue * vit;
        long maxSp = (long)pc.WisSPValue * wis;

        var d = new Dictionary<byte, int>
        {
            [23] = pc.APValue * str,                                   // Physical Attack
            [25] = pc.MAValue * intel,                                 // Magic Attack
            [26] = pc.VitDefenseValue * vit,                           // Defense (naked)   (+armor elsewhere)
            [60] = pc.AgiCrticalValue * agi,                           // Critical
            [61] = pc.StrParryValue * str + pc.IntParryValue * intel,  // Parry / Guard
            [59] = pc.AgiDodgeValue * agi + 1,                         // Dodge / Evade (retail adds a flat +1)
            [58] = pc.StrHitValue * str + pc.IntHitValue * intel + pc.HitValue * agi + 1,  // Hit / Precision (HitValue scales with AGI, plus a flat +1)
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
        // Measured in-game (naked, titles off) across classes: every class's base primary is 1 for STR..VIT and 0 for
        // LUK. Retail's 2498 keeps the LUK slot (blob+24) at 0 in every captured frame, so the base is (1,1,1,1,1,0).
        // The Standard_Stat Base_* columns (e.g. 10/7/0/0/3) are legacy starting-license values, not the live base; all
        // stat identity comes from level/job/gear/allocation on top of this. The row is still required to exist (null =
        // the class has no row, caller keeps its fallback).
        return (1, 1, 1, 1, 1, 0);
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

    /// <summary>
    /// A character's gear-adjusted combat inputs the SAME way the 2498 sheet resolves them, given the already
    /// gear-and-title-folded <paramref name="eq"/> and the class <paramref name="pcRow"/>. Gear raises every stat
    /// THROUGH the primaries (trait types 0..5); the effective primaries then feed <see cref="ComputeBaseStats"/>.
    /// Max HP/SP add the flat trait-21/22 rows; Defense/Crit/Parry are naked-derived + the gear's flat (26/60/61);
    /// Vigor (62) and Pen (94) are gear-only. The host resolves the gear, the titles and the row and calls this.
    /// </summary>
    public static EffectiveStats EffectivePlayerStats(Character c, EquipStats eq, PCBaseStatus? pcRow, IReadOnlyList<NpcLevelScaling> levelScaling)
    {
        int P(int baseStat, byte traitType) => Math.Clamp(eq.Apply(traitType, baseStat), 0, ushort.MaxValue);
        int str = P(c.Str, 0), agi = P(c.Agi, 1), intel = P(c.Int, 2),
            wis = P(c.Wis, 3), vit = P(c.Vit, 4), luk = P(c.Luk, 5);
        var bs = ComputeBaseStats(pcRow, levelScaling, c.Level, str, agi, intel, wis, vit, luk);
        // Trait 21 / 22 rows add flat AND percent to the class max, as on the sheet: (base + flat) * (1 + pct/100),
        // the same (flat-then-percent) rule EquipStats.Apply uses for the primaries. A class with no row keeps its 0 max.
        uint maxHp = bs.MaxHp > 0 ? (uint)Math.Clamp((long)Math.Round((bs.MaxHp + eq.FlatOf(21)) * (1f + eq.PercentOf(21) / 100f)), 0, uint.MaxValue) : bs.MaxHp;
        ushort maxSp = bs.MaxHp > 0 ? (ushort)Math.Clamp((long)Math.Round((bs.MaxSp + eq.FlatOf(22)) * (1f + eq.PercentOf(22) / 100f)), 0, ushort.MaxValue) : bs.MaxSp;
        int defense = bs.Derived.GetValueOrDefault((byte)26) + eq.FlatOf(26);
        int vigor = eq.FlatOf(62);
        int crit = bs.Derived.GetValueOrDefault((byte)60) + eq.FlatOf(60);
        int parry = bs.Derived.GetValueOrDefault((byte)61) + eq.FlatOf(61);
        int pen = eq.FlatOf(94);
        // PvP Damage Increase (71) / PvP Damage Reduction (72): sum the RAW trait values across gear (PercentOf
        // divided each line by 100, so x100 recovers the raw sum), then run each through the diminishing curve.
        float pvpInc = PvpDamageRatePct(eq.PercentOf(71) * 100f);
        float pvpRed = PvpDamageRatePct(eq.PercentOf(72) * 100f);
        // Gear flat Attack Power / Magic Power (trait 23 / 25): the AoV ATKBonus, added to APValue·STR / MAValue·INT
        // at damage time. The 2498 sheet already merges these into the shown Attack, so combat must too.
        int atkBonus = eq.FlatOf(23), matkBonus = eq.FlatOf(25);
        // General damage increase (trait 75 Attak_Damage_Rate) and damage-taken reduction (trait 51 DamageDown):
        // all-content gear stats (no PVP qualifier, so they apply in PvE AND PvP, unlike 71/72). Already-summed
        // percentage points; DamageDown is stored negative. The client never puts these on the stat sheet (item
        // tooltip only), so the server applies the literal percent as a linear final multiplier (no curve).
        float dmgInc = eq.PercentOf(75);
        float dmgRed = eq.PercentOf(51);
        return new EffectiveStats(str, agi, intel, wis, vit, luk, maxHp, maxSp, defense, vigor, crit, parry, pen, pvpInc, pvpRed, atkBonus, matkBonus, dmgInc, dmgRed);
    }

    /// <summary>
    /// PvP Damage Increase / Reduction (trait 71 / 72) diminishing-returns curve: converts the summed RAW gear stat
    /// to a percent, <c>100·raw/(raw + 5500)</c> (= <c>raw/(0.01·raw + 55)</c>). It is above the old linear
    /// <c>raw/100</c> below raw 4500 (crossover at exactly 45%) and below it after, and asymptotes at 100% FROM
    /// BELOW, so PvP damage is never actually capped yet practically never reaches 100% (raw 11400 = 67.46%). The
    /// shape matches the AoV Defense rate's 0.01 coefficient (both top out at 100%). Operator "CLS Formula",
    /// verified exact against its full 0..11400 table.
    /// </summary>
    public static float PvpDamageRatePct(float rawSum) => rawSum <= 0f ? 0f : 100f * rawSum / (rawSum + 5500f);
}
