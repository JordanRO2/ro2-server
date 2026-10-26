using RO2.Worlds.Domain;

namespace RO2.Worlds.Application;

/// <summary>
/// The decision of a world entry (the login burst after RMI 1417, or the burst of a cross-map warp or glider landing), over
/// the pure <see cref="WorldEntryPlan"/>. It decides where the player enters, whether a supplied trait sheet replaces the
/// computed one, and which steps of the plan can be sent at all. The host sends every message (the encrypted 2310 and 2588
/// included), builds every body, reads the clock and keeps the original step order; the steps whose gate needs data the host
/// loads (the craft index table, the skill list) stay gated by the host.
/// </summary>
public static class WorldEntryUseCase
{
    /// <summary>The map and position a player enters at.</summary>
    public readonly record struct EntrySpawn(uint MapId, float X, float Y, float Z);

    /// <summary>What a world entry is asked, as plain facts the host read from the character and its loaded documents.</summary>
    /// <param name="IsWarp">The entry is a cross-map warp or glider landing rather than the first entry after login.</param>
    /// <param name="ResolvedSpawn">The spawn the character's stored position resolves to (<see cref="SpawnRules.ResolveSpawnPosition"/>).</param>
    /// <param name="WarpSpawn">The destination of the warp, or null; only a warp honours it.</param>
    /// <param name="HasWarpTraitSheet">The warp carries a trait sheet of the exact body length that it wants sent as is.</param>
    /// <param name="HasGameData">The game-data catalog is loaded.</param>
    /// <param name="HasInventory">The inventory item list was supplied.</param>
    /// <param name="IsNovice">The character wires as a novice (no profession job), the only one sent a craft index.</param>
    /// <param name="CardBookCharged">The card book has a charge time to report.</param>
    public readonly record struct EntryRequest(
        bool IsWarp,
        EntrySpawn ResolvedSpawn,
        EntrySpawn? WarpSpawn,
        bool HasWarpTraitSheet,
        bool HasGameData,
        bool HasInventory,
        bool IsNovice,
        bool CardBookCharged);

    /// <summary>The decision: where, which trait sheet and the steps after the pre-map-load burst, in send order.</summary>
    /// <param name="Mode">The entry mode the steps come from.</param>
    /// <param name="Spawn">The map and position the entry sends (in 2310 and 2311).</param>
    /// <param name="UseWarpTraitSheet">Send the trait sheet the warp supplied instead of the computed one.</param>
    /// <param name="AfterPreMapLoad">
    /// The steps after <see cref="WorldEntryPlan.PreMapLoad"/>, in send order, without the optional steps whose static gate
    /// failed. A step that stays may still send nothing (the host gates it on loaded data).
    /// </param>
    public sealed record EntryDecision(
        WorldEntryMode Mode,
        EntrySpawn Spawn,
        bool UseWarpTraitSheet,
        IReadOnlyList<WorldEntryStep> AfterPreMapLoad);

    /// <summary>Decides one world entry.</summary>
    public static EntryDecision Decide(EntryRequest request)
    {
        var spawn = request.IsWarp && request.WarpSpawn is { } warp ? warp : request.ResolvedSpawn;
        var useWarpTrait = request.IsWarp && request.HasWarpTraitSheet;

        var plan = WorldEntryPlan.For(request.IsWarp ? WorldEntryMode.Warp : WorldEntryMode.Login);
        var steps = plan.AfterPreMapLoad.Where(step => StepApplies(step, request)).ToArray();
        return new EntryDecision(plan.Mode, spawn, useWarpTrait, steps);
    }

    /// <summary>
    /// The static gate of an optional step: the craft index goes to a novice with a catalog, the card book charge only when
    /// there is one to report, and the card registrations only with both an inventory and a catalog. Every other step is
    /// always part of its plan.
    /// </summary>
    private static bool StepApplies(WorldEntryStep step, EntryRequest request) => step switch
    {
        WorldEntryStep.CraftIndex => request.IsNovice && request.HasGameData,
        WorldEntryStep.CardBookCharge => request.CardBookCharged,
        WorldEntryStep.RegisterCardBook => request.HasInventory && request.HasGameData,
        _ => true,
    };

    /// <summary>
    /// The talent points a login reports when there is no catalog to price the learned skills: the points a level grants
    /// (one per level after the first) minus the levels already spent, clamped to a byte.
    /// </summary>
    public static byte TalentPointsWithoutCatalog(int level, int learnedLevelSum)
        => (byte)Math.Clamp((level - 1) - learnedLevelSum, 0, 255);
}
