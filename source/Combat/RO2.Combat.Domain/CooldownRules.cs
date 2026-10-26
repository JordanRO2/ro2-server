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

    /// <summary>
    /// Ends <paramref name="group"/>'s cooldown in <paramref name="cooldowns"/> (a per-group expiry map) when it
    /// is still cooling at <paramref name="nowUtc"/>; false, and nothing removed, when it was absent or already
    /// lapsed.
    /// </summary>
    public static bool TryEnd(Dictionary<uint, DateTime> cooldowns, uint group, DateTime nowUtc)
    {
        if (!cooldowns.TryGetValue(group, out var expiry) || expiry <= nowUtc)
            return false;
        cooldowns.Remove(group);
        return true;
    }

    /// <summary>
    /// The groups a reset-all cast clears, in ascending order, removed from <paramref name="cooldowns"/>: every
    /// group still cooling at <paramref name="nowUtc"/>, other than <paramref name="castGroup"/> (the cast's own),
    /// that <paramref name="isResettable"/> accepts.
    /// </summary>
    public static List<uint> TakeResettable(Dictionary<uint, DateTime> cooldowns, uint castGroup, DateTime nowUtc,
        Func<uint, bool> isResettable)
    {
        var groups = new List<uint>();
        foreach (var (group, expiry) in cooldowns)
            if (group != castGroup && expiry > nowUtc && isResettable(group))
                groups.Add(group);
        groups.Sort();
        foreach (var group in groups)
            cooldowns.Remove(group);
        return groups;
    }
}
