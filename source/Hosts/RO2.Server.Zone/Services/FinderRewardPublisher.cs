using Microsoft.Extensions.Logging;
using RO2.DungeonFinder.Application;
using RO2.DungeonFinder.Domain;
using RO2.Inventory.Domain;
using RO2.Network.Protocol;

namespace RO2.Server.Zone.Services;

/// <summary>
/// The session-bound side effects of paying a finder run: the quest objective, khara and the 2333 pickup per landed stack,
/// and the 2816 completion notice. Scoped, so <see cref="KharaService"/> gets the paying scope's services. Both packets go out
/// through <c>SendRmiAsync</c>, as before the carve.
/// </summary>
public sealed class FinderRewardPublisher(PlayerStateManager players, BroadcastService broadcast, IQuestObjectiveEvents quests,
    IServiceProvider services, ILogger<FinderRewardPublisher> logger) : IFinderRewardPublisher
{
    public async Task ItemLanded(uint characterId, uint itemId, int count, IReadOnlyList<InventoryItem> landedStacks)
    {
        if (Resolve(characterId) is not var (session, player)) return;
        await quests.OnItemGainedAsync(session, player, itemId, count);
        await KharaService.OnHeldItemChangedAsync(session, player, itemId, services, logger);
        foreach (var stack in landedStacks)
            await session.SendRmiAsync(ZoneS2COpcodes.OnPickupItem,
                LootNotifyPackets.BuildPickupItem2333(ItemPackets.BuildItemRecord126(stack),
                    pickupKind: RewardPolicy.PickupKindFinderReward));
    }

    public async Task Completed(uint characterId, uint listId, CompleteGiftState state)
    {
        if (Resolve(characterId) is not var (session, _)) return;
        await session.SendRmiAsync(ZoneS2COpcodes.NfyRandomDungeonCompleteGift,
            DungeonFinderPackets.BuildCompleteGift2816(listId,
                state == CompleteGiftState.Mailed ? DungeonFinderPackets.CompleteGiftMailed : DungeonFinderPackets.CompleteGiftInBag));
    }

    private (RO2.Network.ProudNet.ProudSession Session, Models.PlayerState Player)? Resolve(uint characterId)
        => players.GetPlayerByCharacterId(characterId) is { } p && broadcast.GetSession(p.SessionId) is { } s ? (s, p) : null;
}
