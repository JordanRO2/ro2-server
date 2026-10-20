using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using RO2.Domain.GameData;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Removes the effects that a skill reset ends. Called right after the reset's 2544 OnInitTalentPoint and before the scroll's 2329.
/// Only effects whose Effect_Table.Init_Handling is 0 are removed, in ascending effect id, through EffectService.
/// Riding (effect 100) is also ended.
/// </summary>
internal static class TalentResetEffects
{
 /// <summary>Effect ids with Init_Handling 0, loaded once per store.</summary>
    private static readonly ConditionalWeakTable<GameDataStore, HashSet<uint>> EndedByResetByStore = new();

 /// <summary>
 /// True when a skill reset ends the effect. Unknown ids are kept.
 /// </summary>
    internal static bool IsEndedByReset(GameDataStore store, uint effectId)
        => EndedByResetByStore.GetValue(store, static s =>
        {
            var ids = new HashSet<uint>();
            foreach (var row in s.EffectTable)
            {
                if (row.Init_Handling == 0)
                    ids.Add(row.ID);
            }
            return ids;
        }).Contains(effectId);

 /// <summary>
 /// Distinct ids of the player's active effects that a reset ends, ascending.
 /// Taken as a snapshot under the effect list lock.
 /// </summary>
    internal static List<uint> EffectsEndedByReset(PlayerState player, GameDataStore store)
    {
        var ids = new SortedSet<uint>();
        lock (player.ActiveEffects)
        {
            foreach (var e in player.ActiveEffects)
            {
                if (IsEndedByReset(store, e.EffectId))
                    ids.Add(e.EffectId);
            }
        }
        return ids.ToList();
    }

 /// <summary>
 /// Removes every effect listed by <see cref="EffectsEndedByReset"/>, each through <see cref="EffectService.RemovePlayerEffectAsync"/>,
 /// and drops the ride after effect 100. Call right after the reset's 2544.
 /// </summary>
 /// <returns>The number of effects removed.</returns>
    internal static async Task<int> DropInitEffectsAsync(ProudSession session, PlayerState player, GameDataStore store,
        IServiceProvider services)
    {
        var ended = EffectsEndedByReset(player, store);
        if (ended.Count == 0) return 0;

        var effects = services.GetRequiredService<EffectService>();
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
