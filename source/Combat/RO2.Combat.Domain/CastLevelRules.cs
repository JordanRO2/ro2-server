namespace RO2.Combats.Domain;

/// <summary>Pure cast-level clamping: the wire level never exceeds the learned level (class skills) nor the table cap.</summary>
public static class CastLevelRules
{
    /// <summary>
    /// Clamps <paramref name="wireLevel"/> (the client-sent cast level): for a class skill, never above
    /// <paramref name="learnedLevel"/> (an unlearned class skill clamps to 0 = no skill rate); and never above
    /// <paramref name="tableLimit"/> when the group has a Talent_Table cap. The host does the skill-id decode and
    /// the learned/table lookups.
    /// </summary>
    public static byte Clamp(byte wireLevel, bool isClassSkill, byte learnedLevel, byte? tableLimit)
    {
        if (isClassSkill) wireLevel = Math.Min(wireLevel, learnedLevel);
        if (tableLimit is { } max) wireLevel = Math.Min(wireLevel, max);
        return wireLevel;
    }
}
