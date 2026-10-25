using RO2.Characters.Domain;
namespace RO2.Characters.Domain;

/// <summary>Pure character stat rules that need no game data: the percent-trait divisor and the bonus-point curve.</summary>
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
}
