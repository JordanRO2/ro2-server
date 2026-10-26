using RO2.GameData;

namespace RO2.Worlds.Domain;

/// <summary>
/// Port the World Domain rules use to read table data (Map_List, Position_Info, NPCInfo). The host implements it over the
/// loaded game data.
/// </summary>
public interface IWorldCatalog
{
 /// <summary>The Map_List row for <paramref name="mapIndex"/>, or <c>null</c> when the map is not in the list.</summary>
    MapData? GetMap(uint mapIndex);

 /// <summary>The Position_Info row <paramref name="positionId"/>, or <c>null</c> when it is not in the table.</summary>
    PositionData? GetPosition(uint positionId);

 /// <summary>The NPCInfo row <paramref name="npcId"/>, or <c>null</c> when it is not in the table.</summary>
    NpcData? GetNpc(uint npcId);
}
