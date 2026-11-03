namespace RO2.Recruitments.Domain;

/// <summary>A recruit notice posted by a character (the party-matching recruit board, 1554).</summary>
/// <param name="CharacterId">Poster's character ID.</param>
/// <param name="Category">Category, from the 1554 body.</param>
/// <param name="Difficulty">Difficulty, from the 1554 body.</param>
/// <param name="Destination">Destination, from the 1554 body.</param>
/// <param name="Title">Title, decoded up to its terminator.</param>
public sealed record RecruitNotice(uint CharacterId, byte Category, byte Difficulty, uint Destination, string Title);

/// <summary>Search filter carried by a 1560 request.</summary>
public readonly record struct RecruitFilter(byte Difficulty, byte Category, ushort Page, uint Destination);

/// <summary>Recruit-board rules.</summary>
public static class RecruitRules
{
    /// <summary>Filter value that matches every category, difficulty or destination (the "All" combo item).</summary>
    private const int FilterAny = 0;

    /// <summary>
    /// Tests whether a notice passes a search filter. A value of 0 in the filter matches any value.
    /// </summary>
    public static bool Matches(RecruitNotice p, RecruitFilter f)
        => (f.Difficulty == FilterAny || p.Difficulty == f.Difficulty)
        && (f.Category == FilterAny || p.Category == f.Category)
        && (f.Destination == FilterAny || p.Destination == f.Destination);
}
