using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Departure sequence sent to a player between the answer to a map-change request and the map-load burst.
/// </summary>
/// <remarks>
/// Order: the answer, removal of the effects that a warp ends, removal of any ride, closing of an open Illusion Pyramid,
/// the 2938 departure packet and the self 2505, then the lines for the effects that a warp keeps, and finally the
/// map-load burst.
/// Shared by the requests that leave through a 2938: 1615 (multi-teleport NPC), 1371 (warp) and 1528 (glider teleport).
/// </remarks>
public static class WarpDeparture
{
 /// <summary>Effect ids whose Warp_Handling is 0, per loaded store.</summary>
    private static readonly ConditionalWeakTable<GameDataStore, HashSet<uint>> DroppedOnWarpByStore = new();

 /// <summary>
 /// True when the effect table says a warp ends <paramref name="effectId"/>. Unknown ids are kept.
 /// </summary>
    internal static bool IsDroppedOnWarp(GameDataStore store, uint effectId)
        => DroppedOnWarpByStore.GetValue(store, static s =>
        {
            var ids = new HashSet<uint>();
            foreach (var row in s.EffectTable)
            {
                if (row.Warp_Handling == 0)
                    ids.Add(row.ID);
            }
            return ids;
        }).Contains(effectId);

 /// <summary>
 /// Runs the full departure for a request that is answered with a 2938 before the map-load burst.
 /// </summary>
 /// <remarks>
 /// The caller sends its answer first and <see cref="WarpService.WarpAsync"/> after, which then finds the ride dropped and the pyramid closed.
 /// The kept-effect lines need the character sheet. Without a character they are skipped, and the map-load burst restates the sheet.
 /// </remarks>
    public static async Task DepartAsync(ProudSession session, PlayerState player, IServiceProvider services,
        ILogger logger, CancellationToken ct)
    {
        var store = services.GetRequiredService<GameDataStore>();
        var effects = services.GetRequiredService<EffectService>();

        await DropWarpEffectsAsync(player, store, effects);
        await RidingService.DropAsync(session, player, services);
        await IllusionPyramidService.CloseOnDepartureAsync(session, player, services, logger, ct);

        await session.SendInternalRmiAsync(ZoneS2COpcodes.AnsSiegeArmsUseEnd, SessionExitPackets.Ack2938);
        await session.SendInternalRmiAsync(ZoneS2COpcodes.OnPC_Out,
            PlayerSpawnService.BuildActorOutList(player.EntityId, new[] { player.EntityId }));

        if (!HasKeptEffectLines(player, store, effects))
            return;

        var character = player.Character
            ?? await services.GetRequiredService<ICharacterRepository>().GetByIdAsync(player.CharacterId);
        if (character is null)
        {
            logger.LogWarning("{Name} departure: no character {Id}, the kept-effect 2499 runs are not sent",
                player.Name, player.CharacterId);
            return;
        }
        RO2.Server.Zone.Handlers.Rmi.AnsMapLoadingHandler.SyncLiveLevel(player, character);
        var equipped = await services.GetRequiredService<IInventoryRepository>().GetEquipmentAsync(player.CharacterId);
        await SendKeptEffectLinesAsync(session, player, store, effects, character, equipped);
    }

 /// <summary>
 /// Removes every active effect of <paramref name="player"/> whose Warp_Handling is 0, in the order they were applied.
 /// </summary>
 /// <remarks>
 /// Call it before the map changes, because the removal is sent to the current map.
 /// </remarks>
    public static async Task DropWarpEffectsAsync(PlayerState player, GameDataStore store, EffectService effects)
    {
        var dropped = new List<uint>();
        lock (player.ActiveEffects)
        {
            foreach (var e in player.ActiveEffects)
            {
                if (IsDroppedOnWarp(store, e.EffectId) && !dropped.Contains(e.EffectId))
                    dropped.Add(e.EffectId);
            }
        }

        foreach (var id in dropped)
            await effects.RemovePlayerEffectAsync(player, id);
    }

 /// <summary>
 /// For each effect a warp keeps that has stat-modifier rows, sends its trait-change lines followed by one in-world character sheet.
 /// </summary>
 /// <remarks>
 /// Call it after the self 2505 and before the map-load burst.
 /// </remarks>
    public static async Task SendKeptEffectLinesAsync(ProudSession session, PlayerState player, GameDataStore store,
        EffectService effects, Character character, IReadOnlyList<InventoryItem> equipped)
    {
        foreach (var effect in KeptEffects(player, store))
        {
            var lines = effects.BuildTraitChangeLines(effect.EffectId, effect.EffectLevel);
            if (lines.Count == 0)
                continue;
            foreach (var line in lines)
                await session.SendInternalRmiAsync(ZoneS2COpcodes.OnPcTraitChange, line);
            await session.SendInternalRmiAsync(ZoneS2COpcodes.OnPcTrait,
                effects.BuildInWorldPcTrait(player, character, equipped));
        }
    }

    private static bool HasKeptEffectLines(PlayerState player, GameDataStore store, EffectService effects)
    {
        foreach (var effect in KeptEffects(player, store))
        {
            if (effects.BuildTraitChangeLines(effect.EffectId, effect.EffectLevel).Count > 0)
                return true;
        }
        return false;
    }

    private static List<ActiveEffect> KeptEffects(PlayerState player, GameDataStore store)
    {
        var kept = new List<ActiveEffect>();
        lock (player.ActiveEffects)
        {
            foreach (var e in player.ActiveEffects)
            {
                if (!IsDroppedOnWarp(store, e.EffectId))
                    kept.Add(e);
            }
        }
        return kept;
    }
}
