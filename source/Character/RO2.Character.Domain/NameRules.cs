using System.Text;
using System.Text.RegularExpressions;
using NameFilterRow = RO2.GameData.Tables.NameFilter;

namespace RO2.Characters.Domain;

/// <summary>Reason a character name is refused. Each value maps to a distinct error code in the name-check response.</summary>
public enum NameRefusal
{
    /// <summary>The name is acceptable.</summary>
    None = 0,
    /// <summary>The name is empty, too short or long, or contains disallowed characters.</summary>
    Format,
    /// <summary>The name matches an entry in the prohibited-name list (name_filter).</summary>
    Prohibited,
}

/// <summary>Reason a RENAME is refused, grouped by the two rename result codes (15402 vs 15403).</summary>
public enum RenameNameRefusal
{
    /// <summary>The new name passes the rename rules.</summary>
    None = 0,
    /// <summary>Too short, the character's current name, no scroll, or a name already taken (result 15402).</summary>
    TakenOrShort,
    /// <summary>Invalid characters, a prohibited name, or over the rename length limit (result 15403).</summary>
    NotAllowed,
}

/// <summary>
/// Pure character-name validation (format + prohibited list) shared by the name check, creation and the rename
/// scroll, over the name_filter rows passed in. No database, no game-data store, no wire.
/// </summary>
/// <remarks>
/// Format: 3 to 16 characters from [A-Za-z0-9_], with no leading, trailing or doubled underscore. A name is
/// prohibited when it equals a name_filter entry after NFKC folding and ignoring case; entries are not matched as
/// substrings, except the <see cref="ContainedRows"/> entries (e.g. "GM"), matched case-sensitively on the NFKC form.
/// </remarks>
public static partial class NameRules
{
    /// <summary>Shortest accepted name length.</summary>
    public const int MinLength = 3;

    /// <summary>Longest accepted name length (also used by character creation).</summary>
    public const int MaxLength = 16;

    /// <summary>Longest name a rename accepts; separate from <see cref="MaxLength"/>, which also applies to creation.</summary>
    public const int MaxRenameLength = 14;

    /// <summary>Name_filter rows that also refuse a name when they appear inside it (case-sensitive on the NFKC form).</summary>
    internal static readonly string[] ContainedRows = ["GM"];

    [GeneratedRegex(@"^[a-zA-Z0-9_]+$")]
    private static partial Regex AllowedCharsRegex();

    /// <summary>
    /// Validates a name's format and prohibited-list membership against <paramref name="filter"/> (the name_filter rows).
    /// </summary>
    /// <returns>The refusal reason (<see cref="NameRefusal.None"/> when accepted) and a log reason.</returns>
    public static (NameRefusal refusal, string? reason) Check(string? name, IReadOnlyList<NameFilterRow>? filter)
    {
        if (string.IsNullOrWhiteSpace(name))
            return (NameRefusal.Format, "empty");

        if (name.Length < MinLength)
            return (NameRefusal.Format, $"too short (min {MinLength})");

        if (name.Length > MaxLength)
            return (NameRefusal.Format, $"too long (max {MaxLength})");

        if (!AllowedCharsRegex().IsMatch(name))
            return (NameRefusal.Format, "invalid characters");

        if (name.StartsWith('_') || name.EndsWith('_'))
            return (NameRefusal.Format, "cannot start/end with underscore");

        if (name.Contains("__"))
            return (NameRefusal.Format, "consecutive underscores");

        if (IsProhibited(name, filter))
            return (NameRefusal.Prohibited, "prohibited name (name_filter)");

        if (ContainsProhibitedWord(name, filter) is { } word)
            return (NameRefusal.Prohibited, $"contains the prohibited word \"{word}\" (name_filter)");

        return (NameRefusal.None, null);
    }

    /// <summary>
    /// The rename-specific name check: too short or the character's own name give <see cref="RenameNameRefusal.TakenOrShort"/>;
    /// invalid characters, a prohibited name, or a name over <see cref="MaxRenameLength"/> give
    /// <see cref="RenameNameRefusal.NotAllowed"/>. The scroll and uniqueness checks are the Application flow's.
    /// </summary>
    /// <returns>The rename refusal (<see cref="RenameNameRefusal.None"/> when the name itself passes) and a log reason.</returns>
    public static (RenameNameRefusal refusal, string? reason) CheckRename(string newName, string currentName,
        IReadOnlyList<NameFilterRow>? filter)
    {
        if (newName.Length < MinLength)
            return (RenameNameRefusal.TakenOrShort, $"too short (min {MinLength})");

        var (refusal, reason) = Check(newName, filter);
        if (refusal != NameRefusal.None)
            return (RenameNameRefusal.NotAllowed, reason);

        if (newName.Length > MaxRenameLength)
            return (RenameNameRefusal.NotAllowed, $"too long for a rename (max {MaxRenameLength})");

        if (string.Equals(newName, currentName, StringComparison.Ordinal))
            return (RenameNameRefusal.TakenOrShort, "same name");

        return (RenameNameRefusal.None, null);
    }

    private static bool IsProhibited(string name, IReadOnlyList<NameFilterRow>? filter)
    {
        if (filter is null || filter.Count == 0) return false;
        var key = Fold(name);
        foreach (var row in filter)
        {
            if (!string.IsNullOrWhiteSpace(row.name) && string.Equals(Fold(row.name), key, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static string? ContainsProhibitedWord(string name, IReadOnlyList<NameFilterRow>? filter)
    {
        if (filter is null || filter.Count == 0) return null;
        var key = name.Trim().Normalize(NormalizationForm.FormKC);
        foreach (var row in filter)
        {
            if (string.IsNullOrWhiteSpace(row.name)) continue;
            var word = row.name.Trim().Normalize(NormalizationForm.FormKC);
            if (Array.IndexOf(ContainedRows, word) >= 0 && key.Contains(word, StringComparison.Ordinal))
                return word;
        }
        return null;
    }

    private static string Fold(string s) => s.Trim().Normalize(NormalizationForm.FormKC).ToUpperInvariant();
}
