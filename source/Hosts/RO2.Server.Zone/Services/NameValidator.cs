using System.Text;
using System.Text.RegularExpressions;
using RO2.GameData;
using NameFilterRow = RO2.GameData.Tables.NameFilter;

namespace RO2.Server.Zone.Services;

/// <summary>Reason a character name is refused. Each value maps to a distinct error code in the name check response.</summary>
public enum NameRefusal
{
 /// <summary>The name is acceptable.</summary>
    None = 0,
 /// <summary>The name is empty, too short or long, or contains disallowed characters.</summary>
    Format,
 /// <summary>The name matches an entry in the prohibited-name list (name_filter).</summary>
    Prohibited,
}

/// <summary>
/// Shared character name validation used by the name check, character creation and the name-change scroll,
/// so a name accepted by one of them is accepted by all.
/// </summary>
/// <remarks>
/// Format: 3 to 16 characters from [A-Za-z0-9_], with no leading, trailing or doubled underscore.
///
/// Prohibited names come from the data pipeline (data/name_filter.json, loaded as GameDataStore.NameFilter),
/// not from code. A name is prohibited when it equals an entry after NFKC folding and ignoring case. Entries
/// are not matched as substrings, with one exception listed under <see cref="ContainedRows"/>.
/// </remarks>
public static partial class NameValidator
{
    public const int MinLength = 3;
    public const int MaxLength = 16;

 /// <summary>
 /// Name_filter rows that also refuse a name when they appear inside it. Matching is case-sensitive on the
 /// NFKC-folded name. Every other row matches the whole name only.
 /// </summary>
    internal static readonly string[] ContainedRows = ["GM"];

    [GeneratedRegex(@"^[a-zA-Z0-9_]+$")]
    private static partial Regex AllowedCharsRegex();

 /// <summary>
 /// Validates a character name (format and prohibited list, without a database check) against
 /// <paramref name="filter"/>, the name_filter rows.
 /// </summary>
 /// <returns>The refusal reason, or <see cref="NameRefusal.None"/> when the name is accepted, and a log reason.</returns>
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
 /// Validates a character name (no database check) against the loaded name_filter table. Used by character
 /// creation and the name-change scroll. The name check calls <see cref="Check"/> with the store it resolves,
 /// so all three paths apply the same rule.
 /// </summary>
 /// <returns>(valid, reason), where reason is null when the name is valid.</returns>
    public static (bool valid, string? reason) ValidateFormat(string? name)
    {
        var (refusal, reason) = Check(name, GameDataStore.Loaded?.NameFilter);
        return (refusal == NameRefusal.None, reason);
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

 /// <summary>Returns the <see cref="ContainedRows"/> word contained in the name, when name_filter carries that row; otherwise null.
 /// Matching is ordinal and case-sensitive on the NFKC form.</summary>
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
