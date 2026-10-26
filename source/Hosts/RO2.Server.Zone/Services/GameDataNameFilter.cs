using RO2.Characters.Application;
using RO2.GameData;
using NameFilterRow = RO2.GameData.Tables.NameFilter;

namespace RO2.Server.Zone.Services;

/// <summary>Backs <see cref="INameFilter"/> with the loaded name_filter rows (the same source as NameValidator).</summary>
internal sealed class GameDataNameFilter : INameFilter
{
    public IReadOnlyList<NameFilterRow>? Rows => GameDataStore.Loaded?.NameFilter;
}
