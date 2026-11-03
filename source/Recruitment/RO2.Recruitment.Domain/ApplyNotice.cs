namespace RO2.Recruitments.Domain;

/// <param name="CharacterId">Character that made the posting.</param>
/// <param name="Category">Category id.</param>
/// <param name="RoleFlags">Role flags.</param>
/// <param name="Difficulty">Difficulty index.</param>
/// <param name="Destination">Destination id.</param>
/// <param name="Title">Title, up to its NUL terminator.</param>
public sealed record ApplyNotice(uint CharacterId, byte Category, byte RoleFlags, byte Difficulty, uint Destination,
    string Title);

/// <summary>The search filter carried by a 1460 request.</summary>
public readonly record struct ApplyFilter(byte Category, byte RoleFlags, byte Difficulty, ushort Page,
    uint Destination);

/// <summary>The apply-side role bitmask: bits 1, 2 and 4 are the three roles, 7 is all of them.</summary>
public static class RoleFlags
{
    /// <summary>All three role bits.</summary>
    public const byte All = 7;

    /// <summary>Whether a filter's roles select a notice's roles; a filter of 0 means any role.</summary>
    public static bool Overlaps(byte posted, byte filter) => filter == 0 || (posted & filter) != 0;
}

/// <summary>Apply-board rules.</summary>
public static class ApplyRules
{
    /// <summary>Number of difficulty levels the client's search form offers (All, Easy, Normal, Hard).</summary>
    public const int DifficultyCount = 4;

    /// <summary>
    /// Category ids of the client's Find_Party_Category table. Every viewer builds its category list from that table,
    /// so a row whose category is missing from it cannot be drawn.
    /// </summary>
    /// <remarks>
    /// This is the client's own table, not <c>GameDataStore.FindPartyCategory</c>. If a client build adds a category,
    /// this list must follow it.
    /// </remarks>
    public static ReadOnlySpan<byte> ClientCategoryIds => [0, 1, 2, 3];

    /// <summary>
    /// Whether every viewer can draw a row with this category and difficulty. A category outside
    /// <see cref="ClientCategoryIds"/> or a difficulty at or above <see cref="DifficultyCount"/> cannot be drawn by the client.
    /// </summary>
    public static bool IsListable(byte category, byte difficulty)
        => difficulty < DifficultyCount && ClientCategoryIds.Contains(category);

    /// <summary>
    /// Whether <paramref name="p"/> matches <paramref name="f"/>. Item 0 of each choice list means "any", and role
    /// flags of 0 mean any role.
    /// </summary>
    public static bool Matches(ApplyNotice p, ApplyFilter f)
        => (f.Category == 0 || p.Category == f.Category)
        && RoleFlags.Overlaps(p.RoleFlags, f.RoleFlags)
        && (f.Difficulty == 0 || p.Difficulty == f.Difficulty)
        && (f.Destination == 0 || p.Destination == f.Destination);
}
