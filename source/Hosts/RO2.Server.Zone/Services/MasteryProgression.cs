using Microsoft.Extensions.DependencyInjection;
using RO2.Characters.Domain;
using RO2.Guilds.Domain;
using RO2.Inventory.Domain;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Shared host step for the job-mastery NPC actions (start, level-up, rank-up): push the updated state to the
/// clients. The master level and rank are not carried by the 2987/2988/2989 answers; the client reads them from the
/// PC view record, so it is re-sent to the map. When the action grew the bonus stat pool (a master level-up grants
/// <see cref="CharacterStats.BonusStatPointsPerMasteryLevel"/> allocatable points) the stat sheet (2595/2498) is
/// re-sent to the player too, so the new points show and can be spent at once.
/// </summary>
internal static class MasteryProgression
{
    public static async Task ApplyAsync(ProudSession session, PlayerState player, Character character,
        IServiceProvider services, BroadcastService broadcast, bool pushStatSheet)
    {
        var equipped = await services.GetRequiredService<IInventoryRepository>().GetEquipmentAsync(player.CharacterId);

        var view = ActorStatePackets.BuildOnPCViewData2315(player, character, equipped,
            services.GetRequiredService<IGuildEmblemRepository>());
        await broadcast.BroadcastToMapAsync(player.MapId, ZoneS2COpcodes.OnPCViewData, w => w.WriteBytes(view));

        if (!pushStatSheet)
            return;

        var trait = services.GetRequiredService<EffectService>().RefreshPcTraitSheet(player, character, equipped);
        await session.SendInternalRmiAsync(ZoneS2COpcodes.OnPcInitTrait, TraitPackets.BuildPcInitTrait2595(character, trait));
        await session.SendInternalRmiAsync(ZoneS2COpcodes.OnPcTrait, trait);
    }
}
