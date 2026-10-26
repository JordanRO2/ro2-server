namespace RO2.Worlds.Domain;

/// <summary>
/// Pure rule for the map and position a character enters the world at (login 1417 and the world-entry 2310/2311 pair),
/// and the default spawn it falls back to.
/// </summary>
public static class SpawnRules
{
 /// <summary>Default starting map.</summary>
    public const uint DefaultMapId = 1101;
 /// <summary>Default spawn X position on the default starting map.</summary>
    public const float DefaultX = 209.34f;
    /// <summary>Default spawn Y position on the default starting map.</summary>
    public const float DefaultY = 268.79f;
    /// <summary>Default spawn Z position on the default starting map.</summary>
    public const float DefaultZ = 48.23f;

 /// <summary>
 /// Resolves the map and position a character enters the world at.
 /// </summary>
 /// <remarks>
 /// Falls back to the default starting map (and its default position) when the stored map is 0 or is not in the map
 /// list (or there is no catalog). A valid map with a stored position of exactly (0,0,0) takes the map row's own
 /// position when that is non-zero, and otherwise stays at (0,0,0).
 /// </remarks>
    public static (uint MapId, float X, float Y, float Z) ResolveSpawnPosition(
        uint mapId, float x, float y, float z, IWorldCatalog? catalog)
    {
        var isValidMap = mapId != 0 && catalog?.GetMap(mapId) != null;

        if (!isValidMap)
        {
            mapId = DefaultMapId;
            x = DefaultX;
            y = DefaultY;
            z = DefaultZ;
        }
        else if (x == 0f && y == 0f && z == 0f)
        {
            var mapData = catalog?.GetMap(mapId);
            if (mapData != null && (mapData.x != 0 || mapData.y != 0 || mapData.z != 0))
            {
                x = mapData.x;
                y = mapData.y;
                z = mapData.z;
            }
        }

        return (mapId, x, y, z);
    }
}
