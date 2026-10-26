using RO2.Worlds.Domain;

namespace RO2.Worlds.Application;

/// <summary>
/// The decisions of a server-driven map change (the warp service): whether the destination exists, where the player lands,
/// whether the map changes, and which messages follow the map-load trigger. Each is pure: the host performs every drop,
/// send, load and mutation in its own original order, with the data loads and their error fall-backs between the sends.
/// </summary>
public static class WarpUseCase
{
    /// <summary>What a warp to a known map resolves to.</summary>
    /// <param name="X">The landing x, after the map's default entry point replaced an all-zero request.</param>
    /// <param name="Y">The landing y.</param>
    /// <param name="Z">The landing z.</param>
    /// <param name="ChangesMap">True when the destination is another map: the host then closes the Illusion Pyramid on departure and sends the pre-map-load burst; otherwise it closes the pyramid on map load.</param>
    /// <param name="MapLoadSteps">The messages from the map-load trigger on, in send order. The host builds each body.</param>
    public readonly record struct WarpDecision(
        float X, float Y, float Z, bool ChangesMap, IReadOnlyList<WorldEntryStep> MapLoadSteps);

    /// <summary>
    /// The warp-mode messages from the map-load trigger on. A warp that has no inventory snapshot (the GM and flight
    /// warps) sends no card book registrations, so that step is not part of it.
    /// </summary>
    public static readonly IReadOnlyList<WorldEntryStep> MapLoadSteps =
        WorldEntryPlan.For(WorldEntryMode.Warp).AfterPreMapLoad
            .Where(step => step != WorldEntryStep.RegisterCardBook)
            .ToArray();

    /// <summary>
    /// Decides a warp to <paramref name="mapId"/>, or null (nothing moves, nothing is sent) when the map is not in the map list.
    /// </summary>
    public static WarpDecision? Decide(IWorldCatalog catalog, uint mapId, float x, float y, float z, uint currentMapId)
    {
        if (catalog.GetMap(mapId) is not { } map) return null;

        var (landX, landY, landZ) = WarpDestinationRules.DefaultEntryPoint(map, x, y, z);
        return new WarpDecision(landX, landY, landZ, mapId != currentMapId, MapLoadSteps);
    }

    /// <summary>
    /// True when a client-driven map change (glider or flight) may move the player server-side: the destination map is in the map list.
    /// </summary>
    public static bool CanCommitClientMapChange(IWorldCatalog catalog, uint mapId) => catalog.GetMap(mapId) is not null;
}
