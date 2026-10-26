namespace RO2.Characters.Domain;

/// <summary>Pure character appearance rules.</summary>
public static class AppearanceRules
{
    /// <summary>
    /// Whether a gender change from <paramref name="oldGender"/> to <paramref name="newGender"/> is blocked by a
    /// worn gender-specific item: true when the gender changes and any worn item requires a sex (1 or 2) other than
    /// the new gender. <paramref name="wornRequireSex"/> is each worn item's Require_Sex.
    /// </summary>
    public static bool GenderChangeBlockedByWornItems(byte oldGender, byte newGender, IEnumerable<byte> wornRequireSex)
    {
        if (oldGender == newGender) return false;
        foreach (var sex in wornRequireSex)
            if (sex is 1 or 2 && sex != newGender) return true;
        return false;
    }
}
