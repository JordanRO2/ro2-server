using NameFilterRow = RO2.GameData.Tables.NameFilter;

namespace RO2.Characters.Application;

/// <summary>The prohibited-name rows (name_filter) a name check runs against, so the flows do not read the store.</summary>
public interface INameFilter
{
    /// <summary>The loaded name_filter rows, or null when none are loaded.</summary>
    IReadOnlyList<NameFilterRow>? Rows { get; }
}
