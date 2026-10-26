using RO2.GameData;
using RO2.Worlds.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>Host adapter of the World Domain's <see cref="IWorldCatalog"/> port over the loaded <see cref="GameDataStore"/>.</summary>
public sealed class GameDataWorldCatalog : IWorldCatalog
{
    private readonly GameDataStore _data;

    public GameDataWorldCatalog(GameDataStore data) => _data = data;

    /// <inheritdoc />
    public MapData? GetMap(uint mapIndex) => _data.GetMap(mapIndex);

    /// <inheritdoc />
    public PositionData? GetPosition(uint positionId) => _data.Positions.TryGetValue(positionId, out var p) ? p : null;

    /// <inheritdoc />
    public NpcData? GetNpc(uint npcId) => _data.Npcs.TryGetValue(npcId, out var n) ? n : null;
}
