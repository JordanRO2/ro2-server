using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using RO2.GameData;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Removes the effects that a player's death ends. Runs after the death notifications are sent: every active
/// effect whose Effect_Table.After_Death_Handling is 0 is removed, in ascending effect id, each with its usual
/// removal updates. Effects with After_Death_Handling 1 are kept.
/// </summary>
/// <remarks>
/// <para>
/// Called from both player death paths: monster and damage-over-time deaths in NpcCombatService, and fall
/// deaths in FallDamageHandler.
/// </para>
/// <para>
/// Removing a riding effect also drops the mount, so the dismount updates follow the removal.
/// </para>
/// </remarks>
internal static class DeathEffects
{
 /// Effect ids whose After_Death_Handling is 0, per loaded store.
    private static readonly ConditionalWeakTable<GameDataStore, HashSet<uint>> EndedByDeathByStore = new();

 /// <summary>
 /// True when the effect table says a death ends <paramref name="effectId"/>. Unknown ids are kept.
 /// </summary>
    internal static bool IsEndedByDeath(GameDataStore store, uint effectId)
        => EndedByDeathByStore.GetValue(store, static s =>
        {
            var ids = new HashSet<uint>();
            foreach (var row in s.EffectTable)
            {
                if (row.After_Death_Handling == 0)
                    ids.Add(row.ID);
            }
            return ids;
        }).Contains(effectId);

 /// <summary>
 /// The distinct ids of the active effects of <paramref name="player"/> that a death ends, in ascending order.
 /// </summary>
    internal static List<uint> EffectsEndedByDeath(PlayerState player, GameDataStore store)
    {
        var ids = new SortedSet<uint>();
        lock (player.ActiveEffects)
        {
            foreach (var e in player.ActiveEffects)
            {
                if (IsEndedByDeath(store, e.EffectId))
                    ids.Add(e.EffectId);
            }
        }
        return ids.ToList();
    }

 /// <summary>
 /// Removes every effect listed by <see cref="EffectsEndedByDeath"/>, each through
 /// <see cref="EffectService.RemovePlayerEffectAsync"/>, and drops the ride when the riding effect is removed.
 /// Returns how many effects were removed; 0 when none is active or the container holds no EffectService.
 /// </summary>
    internal static async Task<int> DropDeathEffectsAsync(ProudSession session, PlayerState player, GameDataStore store,
        IServiceProvider services)
    {
        var ended = EffectsEndedByDeath(player, store);
        if (ended.Count == 0) return 0;

        if (services.GetService<EffectService>() is not { } effects) return 0;
        var removed = 0;
        foreach (var id in ended)
        {
            if (!await effects.RemovePlayerEffectAsync(player, id))
                continue;   
            removed++;
            if (id == RidingService.RidingEffectId)
                await RidingService.DropAsync(session, player, services, effectAlreadyRemoved: true);
        }
        return removed;
    }
}
