namespace RO2.Characters.Domain;

/// <summary>The outcome of pricing a bonus-stat allocation request (1493), before the host maps it to a wire code.</summary>
public enum BonusStatOutcome
{
    /// <summary>Every entry fits the unspent pool and the stat ranges: the request can be applied.</summary>
    Ok,

    /// <summary>The request is malformed (a short vector) or the character cannot be allocated to.</summary>
    BadState,

    /// <summary>A stat id is out of range, or a stat is already at the end of its cost table.</summary>
    OverCap,

    /// <summary>The request costs more than the unspent pool holds.</summary>
    TooFewPoints,
}

/// <summary>One allocation in a 1493 request: a stat id (0..5 = STR, AGI, INT, WIS, VIT, LUK) and how many points.</summary>
public readonly record struct BonusEntry(byte StatType, int Amount);

/// <summary>
/// A parsed 1493 request: the declared entry <paramref name="Count"/> and the entries that were actually present.
/// When the body was too short, <see cref="Entries"/> holds fewer than <see cref="Count"/>, which prices to
/// <see cref="BonusStatOutcome.BadState"/> at the first missing entry.
/// </summary>
public readonly record struct BonusRequest(int Count, IReadOnlyList<BonusEntry> Entries);

/// <summary>The priced outcome: the result, the six bonus values it would leave (STR, AGI, INT, WIS, VIT, LUK), and its cost.</summary>
public readonly record struct BonusPlan(BonusStatOutcome Outcome, int[] Bonus, int TotalCost);

/// <summary>
/// Pure pricing of a bonus-stat allocation: the whole request is priced against the unspent pool without side
/// effects, and is accepted only if every entry fits (all-or-nothing, like retail). The wire layout and the
/// result-code mapping stay in the host.
/// </summary>
public static class BonusStatRules
{
    /// <summary>The six allocated bonus values of <paramref name="c"/> in stat order (STR, AGI, INT, WIS, VIT, LUK).</summary>
    public static int[] BonusOf(Character c) => [c.BonusStr, c.BonusAgi, c.BonusInt, c.BonusWis, c.BonusVit, c.BonusLuk];

    /// <summary>Per-stat bonus-allocation cap before the Master level: at most 50 points may go into one stat.</summary>
    public const int BaseStatAllocationCap = 50;

    /// <summary>Per-stat bonus-allocation cap once the character has started the Master level (MasteryTable
    /// Max_Status): the whole pool may be concentrated into a single stat, up to 999.</summary>
    public const int MasterStatAllocationCap = 999;

    /// <summary>The per-stat bonus cap for a character, lifted from 50 to 999 once it has a Master level.</summary>
    public static int PerStatCap(bool hasMasterLevel) => hasMasterLevel ? MasterStatAllocationCap : BaseStatAllocationCap;

    /// <summary>
    /// Prices the whole <paramref name="request"/> against <paramref name="unspent"/> on a clone of
    /// <paramref name="currentBonus"/>: <see cref="BonusStatOutcome.Ok"/> only when every entry fits, otherwise the
    /// first failing code in order — BadState for a missing entry, OverCap for a stat id above 5, a stat that has
    /// reached <paramref name="perStatCap"/>, or a stat at the end of its cost table, TooFewPoints once the cost
    /// passes the pool. <paramref name="costOf"/> maps a stat's current bonus to the points one more costs (0 =
    /// cannot be raised).
    /// </summary>
    public static BonusPlan Price(BonusRequest request, int[] currentBonus, int unspent, int perStatCap, Func<int, int> costOf)
    {
        var bonus = (int[])currentBonus.Clone();
        int totalCost = 0;
        var result = BonusStatOutcome.Ok;
        for (int i = 0; i < request.Count && result == BonusStatOutcome.Ok; i++)
        {
            if (i >= request.Entries.Count) { result = BonusStatOutcome.BadState; break; }
            var entry = request.Entries[i];
            if (entry.StatType > 5) { result = BonusStatOutcome.OverCap; break; }
            for (int k = 0; k < entry.Amount; k++)
            {
                if (bonus[entry.StatType] >= perStatCap) { result = BonusStatOutcome.OverCap; break; }
                int cost = costOf(bonus[entry.StatType]);
                if (cost <= 0) { result = BonusStatOutcome.OverCap; break; }
                totalCost += cost;
                bonus[entry.StatType]++;
                // Past the pool the request cannot be accepted whatever follows; stopping here also
                // bounds the work a hostile count/amount pair can ask for.
                if (totalCost > unspent) { result = BonusStatOutcome.TooFewPoints; break; }
            }
        }
        return new BonusPlan(result, bonus, totalCost);
    }
}
