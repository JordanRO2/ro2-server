using RO2.GameData;

namespace RO2.Worlds.Domain;

/// <summary>
/// Port the World Domain rules use to read map-table data (Map_List). The host implements it over the loaded game data.
/// </summary>
public interface IWorldCatalog
{
 /// <summary>The Map_List row for <paramref name="mapIndex"/>, or <c>null</c> when the map is not in the list.</summary>
    MapData? GetMap(uint mapIndex);
}
