namespace RO2.Guilds.Domain;

/// <summary>Why a guild name is, or is not, usable. Text-only rules; uniqueness is a registry concern and stays in the host.</summary>
public enum GuildNameVerdict
{
    /// <summary>The name passes every format rule.</summary>
    Ok = 0,
    /// <summary>Fewer than the minimum characters.</summary>
    TooShort,
    /// <summary>More than the maximum characters.</summary>
    TooLong,
    /// <summary>Starts with an ASCII symbol.</summary>
    LeadingSymbol,
    /// <summary>Contains a less-than or greater-than sign.</summary>
    ForbiddenChar,
    /// <summary>Repeats one character three times in a row.</summary>
    RepeatedChar,
}

/// <summary>
/// The guild-name format rules: 2..14 characters, no leading ASCII symbol, no less-than or greater-than sign, no character three times in a row.
/// </summary>
/// <remarks>
/// 14 is the single domain length rule. The create handler's wider read clamp for stored/legacy names is a wire concern and lives in the host.
/// </remarks>
public static class GuildNameRules
{
    /// <summary>Shortest allowed guild name, in characters.</summary>
    public const int MinChars = 2;

    /// <summary>Longest allowed guild name, in characters.</summary>
    public const int MaxChars = 14;

    /// <summary>First code unit outside ASCII. A leading character below it must be a letter, digit or whitespace.</summary>
    private const int FirstNonAscii = 0x80;

    /// <summary>A name may not repeat one character this many times in a row.</summary>
    private const int MaxRepeatRun = 3;

    /// <summary>Applies the format rules in a fixed order: length, leading character, then a per-character scan.</summary>
    public static GuildNameVerdict CheckFormat(string name)
    {
        if (name.Length < MinChars) return GuildNameVerdict.TooShort;
        if (name.Length > MaxChars) return GuildNameVerdict.TooLong;
        var first = name[0];
        if (first < FirstNonAscii && !char.IsAsciiLetterOrDigit(first) && !char.IsWhiteSpace(first)) return GuildNameVerdict.LeadingSymbol;
        for (int i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (c is '<' or '>') return GuildNameVerdict.ForbiddenChar;
            if (i >= MaxRepeatRun - 1 && c == name[i - 1] && c == name[i - 2]) return GuildNameVerdict.RepeatedChar;
        }
        return GuildNameVerdict.Ok;
    }
}
