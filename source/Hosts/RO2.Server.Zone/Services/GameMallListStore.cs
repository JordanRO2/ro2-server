namespace RO2.Server.Zone.Services;

/// <summary>
/// Protocol constants for the game-mall item lists. The per-account list is stored behind
/// <see cref="RO2.Application.Interfaces.IGameMallListRepository"/>.
/// </summary>
public static class GameMallListStore
{
 /// <summary>The list byte that identifies the per-account list in 1593, 1597 and 2865 packets.</summary>
    public const byte AccountList = 2;
}
