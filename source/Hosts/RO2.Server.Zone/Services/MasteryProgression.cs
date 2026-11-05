using Microsoft.Extensions.DependencyInjection;
using RO2.Characters.Domain;
using RO2.Guilds.Domain;
using RO2.Inventory.Domain;
using RO2.Network.Protocol;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Shared host step for the job-mastery NPC actions (start, level-up, rank-up): re-send the player's PC view
/// record so the updated Master level and rank show at once. The level/rank are not carried by the 2987/2988/2989
/// answers; the client reads them from this record (see <see cref="PlayerSpawnService"/>).
/// </summary>
internal static class MasteryProgression
{
    public static async Task ResendViewDataAsync(PlayerState player, Character character, IServiceProvider services,
        BroadcastService broadcast)
    {
        var equipped = await services.GetRequiredService<IInventoryRepository>().GetEquipmentAsync(player.CharacterId);
        var view = ActorStatePackets.BuildOnPCViewData2315(player, character, equipped,
            services.GetRequiredService<IGuildEmblemRepository>());
        await broadcast.BroadcastToMapAsync(player.MapId, ZoneS2COpcodes.OnPCViewData, w => w.WriteBytes(view));
    }
}
