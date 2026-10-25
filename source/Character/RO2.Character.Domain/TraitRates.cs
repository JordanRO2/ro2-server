using RO2.Characters.Domain;
namespace RO2.Characters.Domain;

/// <summary>
/// The six rate traits of the 2498 (OnPcTrait) sheet and the rate formula. A rate slot is derived from a
/// constant and the character's level; the host maps these into the wire blob.
/// </summary>
public static class TraitRates
{
    /// <summary>Trait IDs whose kind-3 effect lines feed the rate slots.</summary>
    public const byte CastRateTrait = 32, CritRateTrait = 33, HitRateTrait = 34,
        VigorRateTrait = 56, AccelRateTrait = 57, PenRateTrait = 95;

    /// <summary>Coefficients of the rate formula denominator, and the scale of a slot in 1/100 %.</summary>
    private const double ConstTerm = 0.005, LevelTerm = 0.2, BaseTerm = 10.0, SlotScale = 100.0;

    /// <summary>Whether <paramref name="traitId"/> is one of the six rate traits.</summary>
    public static bool IsRateTrait(uint traitId)
        => traitId is CastRateTrait or CritRateTrait or HitRateTrait or VigorRateTrait or AccelRateTrait or PenRateTrait;

    /// <summary>
    /// The rate in 1/100 % for a constant <paramref name="c"/>: floor(100·c / (0.005·c + 0.2·level + 10)).
    /// Returns 0 when <paramref name="c"/> is not positive. A level below 1 is treated as 1.
    /// </summary>
    public static int Rate(int c, int effectiveLevel)
    {
        if (c <= 0) return 0;
        var leff = Math.Max(1, effectiveLevel);
        return (int)(SlotScale * c / (ConstTerm * c + LevelTerm * leff + BaseTerm));
    }
}
