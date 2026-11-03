namespace RO2.Companions.Application;

/// <summary>
/// Adds one to a character's counter on a daily/weekly ranking board. The glider flights call it for the Kafra-transport board
/// (type 1: GetOffGliderHandler per mounted ride, GliderTeleportHandler per 1528 flight), behind a port so the glider use cases
/// do not reach the Zone RankingService.
/// </summary>
public interface IRankingCounter
{
    /// <summary>
    /// Adds one to the character's counter on <paramref name="rankingType"/> for the current period. A no-op for a type with no
    /// schedule; a storage error is swallowed (logged by the implementation), never thrown.
    /// </summary>
    Task CountAsync(uint characterId, byte rankingType);
}
