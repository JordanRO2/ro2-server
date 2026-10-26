namespace RO2.Characters.Domain;

/// <summary>
/// Pure out-of-combat vitals maintenance: the per-tick HP/SP regen amount (a flat percent of the maximum, floored
/// at 1). The host owns the tick loop, the vitals lock, the dead-check, the mutation and the 2401 send.
/// </summary>
public static class VitalsRules
{
    /// <summary>HP regen per 1 s tick: ~0.30% of MaxHp.</summary>
    public const float HpRegenPercent = 0.003f;

    /// <summary>SP regen per 1 s tick: ~0.24% of MaxSp.</summary>
    public const float SpRegenPercent = 0.0024f;

    /// <summary>The HP to regen this tick: <c>max(1, (int)(maxHp · 0.003f))</c> — int·float promotes to float, then truncates.</summary>
    public static int HpRegenDelta(int maxHp) => Math.Max(1, (int)(maxHp * HpRegenPercent));

    /// <summary>The SP to regen this tick: <c>max(1, (int)(maxSp · 0.0024f))</c>.</summary>
    public static int SpRegenDelta(int maxSp) => Math.Max(1, (int)(maxSp * SpRegenPercent));
}
