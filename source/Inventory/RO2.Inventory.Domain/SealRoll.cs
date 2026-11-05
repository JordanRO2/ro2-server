namespace RO2.Inventory.Domain;

/// <summary>
/// Rolls a guardian-seal stat line from a group's candidate table. A roll picks one candidate uniformly, then a value
/// uniformly in its inclusive [Min, Max] range (Min may be negative). The table is independent of the accessory, so
/// the same roll applies to a level-1 or a Master-level item. Pure: the caller supplies the candidates (resolved from
/// config) and the RNG, so it is deterministic under a seeded <see cref="System.Random"/> in tests.
/// </summary>
public static class SealRoll
{
    /// <summary>One candidate line: trait kind (1 flat / 2 percent), trait type (TraitInfo.Trait_Type), and the
    /// inclusive signed value range.</summary>
    public readonly record struct Candidate(byte Kind, byte Type, int Min, int Max);

    /// <summary>Rolls one <see cref="SealLine"/>, or null when there are no candidates (an unconfigured group rolls
    /// nothing — the imprint is then refused by the caller rather than granting a blank seal).</summary>
    /// <remarks>
    /// The value is drawn with a TRIANGULAR weighting toward the low end, not a flat uniform draw: a near-cap value is
    /// rare and most rolls are small, so the result does not read as "always maxing". A signed range peaks at 0; a
    /// non-negative range peaks at its floor. The full range stays reachable, just with a falling probability toward
    /// the cap. (A flat draw sits above half the cap half the time; retail's own samples are flat, so flip the two
    /// NextDouble terms back to a single <c>rng.Next(lo, hi + 1)</c> to restore uniform.)
    /// </remarks>
    public static SealLine? Roll(IReadOnlyList<Candidate> candidates, Random rng)
    {
        if (candidates is null || candidates.Count == 0) return null;
        var c = candidates[rng.Next(candidates.Count)];
        int lo = Math.Min(c.Min, c.Max), hi = Math.Max(c.Min, c.Max);
        int value = lo < 0
            ? (int)Math.Round((rng.NextDouble() - rng.NextDouble()) * hi)                            // symmetric, peak at 0
            : lo + (int)Math.Round(Math.Min(rng.NextDouble(), rng.NextDouble()) * (hi - lo));        // peak at the floor
        value = Math.Clamp(value, lo, hi);
        return new SealLine(c.Kind, c.Type, value);
    }
}
