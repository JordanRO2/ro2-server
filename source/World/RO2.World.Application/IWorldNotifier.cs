namespace RO2.Worlds.Application;

/// <summary>One online player a world notice can be sent to: the session to send on and the map the player is on.</summary>
/// <param name="SessionId">The player's session id.</param>
/// <param name="MapId">The map the player is on when the recipients were listed.</param>
public readonly record struct WorldRecipient(uint SessionId, uint MapId);

/// <summary>
/// The outbound notify port of the world flows. The host adapter implements it over the live player registry, the
/// broadcast service and the player sessions, and builds every packet itself, so the wire stays in the host.
/// </summary>
public interface IWorldNotifier
{
    /// <summary>A snapshot of every online player, in the registry's order.</summary>
    IReadOnlyList<WorldRecipient> OnlinePlayers();

    /// <summary>
    /// Sends the world-event stage-change notice for <paramref name="eventId"/> at <paramref name="stage"/> to one player.
    /// Returns false, sending nothing, when the player has no live session.
    /// </summary>
    Task<bool> SendStageChangeAsync(WorldRecipient recipient, uint eventId, byte stage, CancellationToken ct);
}
