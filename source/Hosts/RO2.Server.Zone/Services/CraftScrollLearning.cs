using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using RO2.Domain.GameData;
using RO2.Domain.GameData.Tables;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Handles a crafting blueprint used from the bag. The recipe named by the blueprint's CraftScroll row is
/// learned, the complete recipe list is sent (2538), the zeny change is confirmed (2435), and the caller then
/// consumes the blueprint.
/// </summary>
/// <remarks>
/// <para>
/// The blueprint's CraftScroll row decides the recipe: its ID is the ItemInfo id of the blueprint and its
/// Craft_ID is the Craft_ItemList id it teaches. Items without a CraftScroll row are not blueprints.
/// </para>
/// <para>
/// A blueprint costs nothing; the zeny balance is unchanged. The blueprint is consumed only when the recipe
/// was learned. Nothing is sent and the blueprint is kept when the recipe belongs to another profession, is
/// already known, or the character or store is unavailable.
/// </para>
/// <para>
/// The 2435 currently sends diamonds as 0, because the server keeps no diamond wallet.
/// </para>
/// </remarks>
internal static class CraftScrollLearning
{
    private static readonly ConditionalWeakTable<IReadOnlyList<CraftScroll>, Dictionary<uint, ushort>> ScrollIndex = new();
    private static readonly ConditionalWeakTable<IReadOnlyList<CraftItemList>, Dictionary<ushort, CraftItemList>> RecipeIndex = new();

 /// <summary>
 /// Returns the Craft_ItemList row taught by the blueprint <paramref name="itemId"/>, or null when the item
 /// has no CraftScroll row or the row names no recipe.
 /// </summary>
    internal static CraftItemList? TryResolve(GameDataStore store, uint itemId)
    {
        var scrolls = ScrollIndex.GetValue(store.CraftScroll, static rows =>
        {
            var map = new Dictionary<uint, ushort>(rows.Count);
            foreach (var r in rows) map.TryAdd(r.ID, r.Craft_ID);
            return map;
        });
        if (!scrolls.TryGetValue(itemId, out var craftId)) return null;
        var recipes = RecipeIndex.GetValue(store.CraftItemList, static rows =>
        {
            var map = new Dictionary<ushort, CraftItemList>(rows.Count);
            foreach (var r in rows) map.TryAdd(r.ID, r);
            return map;
        });
        return recipes.GetValueOrDefault(craftId);
    }

 /// <summary>
 /// Learns <paramref name="recipe"/> from blueprint <paramref name="itemId"/> and sends the 2538 and the 2435.
 /// Returns false when the recipe was not learned; nothing is sent in that case and the caller must not
 /// consume the blueprint.
 /// </summary>
    internal static async Task<bool> TryLearnAsync(ProudSession session, PlayerState player, GameDataStore store,
        RO2.Application.Interfaces.ICraftRecipeRepository recipes, CraftItemList recipe, uint itemId, ILogger logger, CancellationToken ct)
    {
        if (player.Character is not { } c)
        {
            logger.LogWarning("{Name} used blueprint {Item} with no character snapshot: not consumed", player.Name, itemId);
            return false;
        }
        byte profession = WorldEntryService.CreationProfessionByte(c);
        if (recipe.ProType_ID != profession)
        {
            logger.LogInformation("{Name} used blueprint {Item} for recipe {Recipe} of profession {Pro}, not theirs ({Own}): not consumed",
                player.Name, itemId, recipe.ID, recipe.ProType_ID, profession);
            return false;
        }

        IReadOnlyList<ushort> learned;
        try
        {
            learned = await recipes.GetAsync(player.CharacterId, ct);
            if (CraftRecipeStore.KnownRecipeIds(store.CraftItemList, profession, learned).Contains(recipe.ID))
            {
                logger.LogInformation("{Name} used blueprint {Item} for recipe {Recipe}, already known: not consumed",
                    player.Name, itemId, recipe.ID);
                return false;
            }
            if (!await recipes.AddAsync(player.CharacterId, recipe.ID, ct))
            {
                logger.LogInformation("{Name} used blueprint {Item} for recipe {Recipe}, already stored (repeated use): not consumed",
                    player.Name, itemId, recipe.ID);
                return false;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "{Name}: could not store recipe {Recipe} from blueprint {Item}; not consumed",
                player.Name, recipe.ID, itemId);
            return false;
        }

        var known = CraftRecipeStore.KnownRecipeIds(store.CraftItemList, profession, learned.Append(recipe.ID));
        await session.SendInternalRmiAsync(ZoneS2COpcodes.OnAckCraftLearn, CraftRecipeStore.BuildAckCraftLearn2538(known), ct);
        
        var zenyBody = ActorVitalsPackets.ApplyZenyDeltaAndBuild(player, 0);
        await session.SendRmiAsync(ZoneS2COpcodes.OnZenyChange, writer => writer.WriteBytes(zenyBody));
        logger.LogInformation("{Name} learned recipe {Recipe} from blueprint {Item}; {Count} recipes known",
            player.Name, recipe.ID, itemId, known.Count);
        return true;
    }
}
