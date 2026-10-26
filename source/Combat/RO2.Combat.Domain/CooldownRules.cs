namespace RO2.Combats.Domain;

/// <summary>Pure skill-cooldown math: the Vigor reduction applied to a table cooldown before 1508 sends and enforces it.</summary>
public static class CooldownRules
{
    /// <summary>
    /// The cooldown a skill actually takes after the caster's Vigor reduction: the table value scaled by
    /// <c>1 - VigorPct/100</c>, where <c>VigorPct = min(75, V / (0.005V + 0.2·max(1,level) + 10))</c>, then clamped
    /// to <c>[0.25·table, table]</c>. Returns 0 for a non-positive table. The double arithmetic and the single final
    /// <c>(float)</c> cast are preserved exactly, because the float is written verbatim into the 2715 reply and is
    /// also what the server enforces (send == enforce).
    /// </summary>
    public static float EffectiveCooldown(float tableCooldown, int level, int vigor)
    {
        if (tableCooldown <= 0f) return 0f;
        int v = Math.Max(0, vigor);
        double vigorPct = Math.Min(75.0, v / (0.005 * v + 0.2 * Math.Max(1, level) + 10.0));
        double reduced = tableCooldown * (1.0 - vigorPct / 100.0);
        return (float)Math.Clamp(reduced, tableCooldown * 0.25, tableCooldown);
    }
}
