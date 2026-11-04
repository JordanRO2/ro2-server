using RO2.GameData;
using RO2.Inventory.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Rolls an item's honed grinding lines for RMI 1676 (Honing / Random Refine). Server-authoritative: the
/// client has no roll logic, it only displays the result. The retail roll is server-side RNG and can never be
/// recovered exactly, so this reproduces the distribution MEASURED from the 133.9M-frame capture corpus
/// (public/retail, 1,006 honed items). See wiki server/capture-corpus.md (Honing = Random Refine) and the
/// memory note honing-roll-model.
/// </summary>
/// <remarks>
/// Two orthogonal axes, both from the measured public distribution:
/// <list type="bullet">
/// <item>ITEM range-rate <c>rr</c> (sets the star): sampled around the measured mean ~914, clamped to the
///   observed [-1000, 2600]; <c>grv = floor(abili_val * (10000 + rr) / 10000)</c> goes to record+69.</item>
/// <item>PER-LINE rolls (signed basis points on each line's Normal_Val): negative-skewed, drawn from the
///   measured percentile table keyed by (prefix/bonus, Normal_Val bucket). Value = ceil(NV*(10000+roll)/10000).</item>
/// </list>
/// Tier picks are from the data: prefix = even tiers sharing one value, capped at the highest even tier whose
/// Grinding_Abili.Abili_Val &lt;= abili_val, hump-shaped below the cap; bonus = any tier, capped at
/// Abili_Val &lt;= ~0.38*abili_val, top-weighted (mode cap-1). Prefix pairs come from the 21
/// Grinding_Prefix_Abili families.
/// <para>
/// APPROXIMATION (flagged): the per-item rollable STAT POOL is not in any table and the corpus could not pin
/// it per item type, so bonus stats are drawn from the prefix family's stats plus a small common pool. The
/// star/tier/roll-value math is data-backed; only which stat lands on a bonus line is approximate.
/// shortcut: bonus stat pool is approximate, upgrade when per-item grinding stat pools are recovered.
/// </para>
/// </remarks>
public static class HoningRoll
{
    public readonly record struct Result(uint Grv, IReadOnlyList<GrindingLine> Lines);

    private const int RrMin = -1000, RrMax = 2600, RateScale = 10000;
    private const double BonusCapFraction = 0.38;
    private const int MaxTier = 26;
    /// <summary>Chance the 5th (last bonus) line is omitted, as ~12% of retail honed items carry only 4 lines.</summary>
    private const double Pos4OmitChance = 0.12;

    // Measured per-line roll percentiles (basis points) from the full-dump public set, as (u, roll) anchors.
    // Index: [prefix?][bucket] where bucket 0 = NV<30, 1 = NV 30-99, 2 = NV>=100.
    private static readonly double[] P = { 0.05, 0.25, 0.50, 0.75, 0.95 };
    private static readonly int[][] PrefixRoll =
    {
        new[] { -2829, -630, 1478, 3392, 7288 },   // NV<30  (positive-biased value floor)
        new[] { -2949, -1937, -1181, 344, 2929 },  // NV 30-99
        new[] { -2541, -1718, -1044, -215, 1006 }, // NV>=100
    };
    private static readonly int[][] BonusRoll =
    {
        new[] { -1485, -294, 862, 1586, 2570 },    // NV<30
        new[] { -1838, -1145, -437, 171, 1335 },   // NV 30-99
        new[] { -1903, -1125, -475, 131, 1694 },   // NV>=100
    };
    private const int PrefixRollFloor = -4800, BonusRollFloor = -2800;

    // Bonus tier draw: weight of (cap - step), steps 0..9, from the measured public bonus distribution.
    private static readonly double[] BonusStepWeight = { 23.3, 35.6, 14.8, 8.9, 4.8, 2.9, 2.3, 2.0, 2.2, 1.2 };
    // Prefix tier draw (even tiers): weight of (cap - 2*step), steps 0..5.
    private static readonly double[] PrefixStepWeight = { 17.5, 28.0, 30.9, 14.4, 7.4, 1.0 };
    // Bonus main-stat (position 2) weights; stats not listed share the flat fallback.
    private static readonly (int stat, double w)[] Pos2Weight =
        { (14, 44), (7, 14.7), (8, 10.3), (4, 5.2), (0, 4), (2, 4) };

    /// <summary>
    /// Rolls a honed result for a template of ability value <paramref name="abiliVal"/>. Returns null when the
    /// grinding tables are not loaded or the item cannot be honed at this ability value (no eligible tier).
    /// </summary>
    public static Result? Roll(uint abiliVal,
        IReadOnlyList<RO2.GameData.Tables.GrindingAbili> abili,
        IReadOnlyList<RO2.GameData.Tables.GrindingPrefixAbili> prefixes, Random rng)
    {
        if (abiliVal == 0 || abili.Count == 0 || prefixes.Count == 0) return null;

        // tier -> Abili_Val ladder (shared across stats), valid (stat,tier) set, and Normal_Val lookup.
        var tierAbiliVal = new Dictionary<int, int>();
        var normalVal = new Dictionary<int, int>();         // abili_id -> Normal_Val
        var statsAtTier = new Dictionary<int, List<int>>();  // tier -> stats present
        foreach (var a in abili)
        {
            int tier = a.Abili_ID % 1000, stat = a.Abili_ID / 1000;
            tierAbiliVal[tier] = a.Abili_Val;
            normalVal[a.Abili_ID] = a.Normal_Val;
            if (!statsAtTier.TryGetValue(tier, out var list)) statsAtTier[tier] = list = new List<int>();
            if (!list.Contains(stat)) list.Add(stat);
        }

        int PrefixCap()
        {
            int cap = 0;
            for (int t = 2; t <= 20; t += 2)
                if (tierAbiliVal.TryGetValue(t, out var av) && av <= abiliVal) cap = t;
            return cap;
        }
        int BonusCap()
        {
            int cap = 0;
            for (int t = 1; t <= MaxTier; t++)
                if (tierAbiliVal.TryGetValue(t, out var av) && av <= abiliVal * BonusCapFraction) cap = t;
            return cap;
        }

        int prefixCap = PrefixCap();
        int bonusCap = BonusCap();
        if (prefixCap == 0 && bonusCap == 0) return null;   // item too low to hone

        // ---- item range-rate -> grv (star axis) ----
        double tri = (rng.NextDouble() + rng.NextDouble()) / 2.0;   // triangular, mean 0.5 (-> rr mean ~800)
        int rr = Math.Clamp((int)Math.Round(tri * (RrMax - RrMin)) + RrMin, RrMin, RrMax);
        uint grv = (uint)((long)abiliVal * (RateScale + rr) / RateScale);

        var lines = new List<GrindingLine>(GrindingLines.MaxLines);

        // ---- prefix pair: one family at one shared even tier <= prefixCap ----
        if (prefixCap >= 2)
        {
            int prefixTier = PickEvenTier(prefixCap, rng);
            var prefixRows = prefixes
                .Where(r => r.Abili_ID_1 % 1000 == prefixTier && r.Abili_ID_2 % 1000 == prefixTier
                            && normalVal.ContainsKey(r.Abili_ID_1) && normalVal.ContainsKey(r.Abili_ID_2)).ToList();
            if (prefixRows.Count > 0)
            {
                var fam = prefixRows[rng.Next(prefixRows.Count)];   // uniform over families at this tier (weighting is approximate anyway)
                lines.Add(MakeLine(fam.Abili_ID_1, prefix: true, normalVal, rng));
                lines.Add(MakeLine(fam.Abili_ID_2, prefix: true, normalVal, rng));
            }
        }

        // ---- bonus triple: pos2 main-stat slot, pos3/4 broader, duplicates allowed, tiers <= bonusCap ----
        if (bonusCap >= 1)
        {
            // stat pool = the prefix family's stats + the common bonus stats, intersected with what exists.
            var pool = new List<int>();
            foreach (var l in lines) if (!pool.Contains(l.Stat)) pool.Add(l.Stat);
            foreach (var (s, _) in Pos2Weight) if (!pool.Contains(s)) pool.Add(s);
            if (pool.Count == 0) pool.Add(0);

            int bonusLines = rng.NextDouble() < Pos4OmitChance ? 2 : 3;   // ~12% drop the 5th line
            for (int i = 0; i < bonusLines; i++)
            {
                int stat = i == 0 ? PickWeighted(Pos2Weight, rng) : pool[rng.Next(pool.Count)];
                int tier = PickBonusTier(bonusCap, rng);
                int aid = stat * 1000 + tier;
                if (!normalVal.ContainsKey(aid))   // (stat,tier) not a real ability: fall back to a valid stat at this tier
                {
                    if (statsAtTier.TryGetValue(tier, out var at) && at.Count > 0)
                        aid = at[rng.Next(at.Count)] * 1000 + tier;
                    if (!normalVal.ContainsKey(aid)) continue;
                }
                lines.Add(MakeLine((ushort)aid, prefix: false, normalVal, rng));
            }
        }

        return lines.Count == 0 ? null : new Result(grv, lines);
    }

    private static GrindingLine MakeLine(ushort abiliId, bool prefix, IReadOnlyDictionary<int, int> normalVal, Random rng)
    {
        int nv = normalVal.TryGetValue(abiliId, out var v) ? v : 0;
        int bucket = nv < 30 ? 0 : nv < 100 ? 1 : 2;
        int roll = SampleRoll(rng.NextDouble(), prefix, bucket);
        return new GrindingLine(abiliId, (short)Math.Clamp(roll, short.MinValue, short.MaxValue));
    }

    /// <summary>Interpolates a roll from the measured (u, roll) percentile anchors; clamps the tails.</summary>
    private static int SampleRoll(double u, bool prefix, int bucket)
    {
        int[] q = (prefix ? PrefixRoll : BonusRoll)[bucket];
        if (u <= P[0]) return prefix ? PrefixRollFloor : BonusRollFloor;
        if (u >= P[^1]) return q[^1];
        for (int i = 1; i < P.Length; i++)
            if (u <= P[i])
            {
                double f = (u - P[i - 1]) / (P[i] - P[i - 1]);
                return (int)Math.Round(q[i - 1] + f * (q[i] - q[i - 1]));
            }
        return q[^1];
    }

    private static int PickEvenTier(int cap, Random rng)
    {
        // step (in even-tier units) below the cap, hump-shaped per PrefixStepWeight.
        int step = PickIndex(PrefixStepWeight, rng);
        return Math.Max(2, cap - 2 * step);
    }

    private static int PickBonusTier(int cap, Random rng)
    {
        int step = PickIndex(BonusStepWeight, rng);
        return Math.Max(1, cap - step);
    }

    private static int PickIndex(double[] weights, Random rng)
    {
        double total = 0; foreach (var w in weights) total += w;
        double r = rng.NextDouble() * total, acc = 0;
        for (int i = 0; i < weights.Length; i++) { acc += weights[i]; if (r < acc) return i; }
        return weights.Length - 1;
    }

    private static int PickWeighted((int stat, double w)[] weights, Random rng)
    {
        double total = 0; foreach (var (_, w) in weights) total += w;
        double r = rng.NextDouble() * total, acc = 0;
        foreach (var (stat, w) in weights) { acc += w; if (r < acc) return stat; }
        return weights[0].stat;
    }
}
