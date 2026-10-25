using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.GameData;
using RO2.GameData.Tables;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Inventory.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1643 ReqPlayTimeRewardGetItem: claims the current play-time reward slot.</summary>
/// <remarks>
/// <para>
/// Play-time progress is kept per account and shared by its characters. The answer (2945) carries the result,
/// the next slot and its remaining seconds.
/// </para>
/// <para>
/// A granted reward is filed into the account gift box (<see cref="AccountGiftStore"/>) and is collected from
/// there. The gift box has no cell limit, so a ready slot is always granted.
/// </para>
/// </remarks>
public class PlayTimeRewardGetItemHandler : ZoneRmiHandlerBase<PlayTimeRewardGetItemHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqPlayTimeRewardGetItem;
    public override string Name => "PlayTimeRewardGetItem";
    public PlayTimeRewardGetItemHandler(IServiceScopeFactory s, ILogger<PlayTimeRewardGetItemHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

 /// <summary>Where one reward item goes: into a held stack (with its new count), or into new stacks.</summary>
    internal sealed record RewardLanding(uint ItemId, int Count, InventoryItem? MergeInto, int MergedCount,
        IReadOnlyList<InventoryItem> NewStacks);

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        
        
        uint requestedSlot = payload.Length >= 4
            ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(payload.Span)
            : 0u;

        var rewards = services.GetRequiredService<PlayTimeRewardStore>();
        rewards.EnsureClockRunning(services);
        var store = services.GetRequiredService<GameDataStore>();
        var chain = PlayTimeRewardChain.Chain(store);
        var progress = await rewards.GetAsync(player, store, Logger, ct);
        if (progress is null)
            return; 

        
        
        var gifts = services.GetRequiredService<IAccountGiftRepository>();
        try
        {
            await gifts.GetLiveAsync(player.AccountId, ServerWallClock.Now().DateTime, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "{Name}: 1643 for slot {Slot} not answered, the gift box could not be read", player.Name, requestedSlot);
            return;
        }

        PlayTimeRewardChain.ClaimResult? result;
        lock (progress.Gate)
            result = PlayTimeRewardChain.Claim(progress, chain, requestedSlot, player.LoginTime, DateTime.UtcNow,
                _ => true);

        if (result is not { } claim)
        {
            Logger.LogWarning("{Name}: 1643 for slot {Slot} not answered, Attendance.json has no Type-1 (play-time) row",
                player.Name, requestedSlot);
            return;
        }

        Logger.LogDebug("{Name} claims play-time slot {Slot}: result {Result}, answer slot {Next} / {Seconds}s",
            player.Name, requestedSlot, claim.Result, claim.Slot, claim.Seconds);

        
        
        
        int? giftsInBox = null;
        if (claim.GrantedRow is { } row)
        {
            giftsInBox = await FileRewardAsync(gifts, player, row);
            await rewards.SaveAsync(player.AccountId, progress, chain, Logger, ct);
        }

        await session.SendRmiAsync(ZoneS2COpcodes.AnsPlayTimeRewardGetItem,
            PlayTimeRewardChain.BuildAnswer(claim.Result, claim.Slot, claim.Seconds), ct);

        if (giftsInBox is > 0)
            await session.SendInternalRmiAsync(ZoneS2COpcodes.NfyAccountGiftReceived, AccountGiftPackets.Build2798(giftsInBox.Value), ct);
    }

 /// <summary>
 /// Files each reward item of <paramref name="row"/> into the account's gift box as a play-time gift.
 /// Returns the box's total gift count afterwards, or null when nothing was filed (empty row or storage error).
 /// </summary>
    private async Task<int?> FileRewardAsync(IAccountGiftRepository gifts, PlayerState player, Attendance row)
    {
        if (player.AccountId == 0)
        {
            
            Logger.LogWarning("{Name}: play-time slot {Slot} granted with no account id; no gift filed", player.Name, row.ID);
            return null;
        }
        var nowUtc = DateTime.UtcNow;
        var filed = new List<AccountGift>(3);
        foreach (var (itemId, count) in new[] { (row.Reward_item1, row.Item_count1), (row.Reward_item2, row.Item_count2), (row.Reward_item3, row.Item_count3) })
        {
            if (itemId == 0 || count <= 0) continue;
            filed.Add(AccountGiftStore.NewGift(itemId, (ushort)Math.Min(count, ushort.MaxValue), AccountGiftStore.SourcePlayTime, nowUtc));
        }
        if (filed.Count == 0) return null;

        try
        {
            int total = await gifts.FileAsync(player.AccountId, filed, ServerWallClock.Now().DateTime);
            Logger.LogInformation("{Name}: play-time slot {Slot} reward filed in the gift box ({Items}), {Total} gift(s) in it",
                player.Name, row.ID, string.Join(", ", filed.Select(g => $"{g.Count}x {g.ItemId}")), total);
            return total;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogError(ex, "{Name}: play-time slot {Slot} reward could not be filed in the gift box", player.Name, row.ID);
            return null;
        }
    }

 /// <summary>
 /// Plans where each reward item of <paramref name="row"/> lands in the bag, without writing anything.
 /// Items are merged into the lowest fitting held stack, otherwise placed in new stacks in the lowest free cells.
 /// Returns null when any item has no room, so the reward is granted in full or not at all.
 /// </summary>
    internal static IReadOnlyList<RewardLanding>? PlanRewardLandings(IReadOnlyList<InventoryItem> owned, uint characterId,
        Attendance row, IReadOnlyDictionary<uint, ItemData> items)
    {
        var rewards = new (uint ItemId, int Count)[]
        {
            (row.Reward_item1, row.Item_count1),
            (row.Reward_item2, row.Item_count2),
            (row.Reward_item3, row.Item_count3),
        };

        var free = BagCapacity.FreeCells(owned, items);
        int nextCell = 0;
        var stacks = owned.Where(i => i.StorageType == 0 && i.Slot >= 0 && i.Count > 0)
            .Select(i => (Item: i, Count: i.Count)).ToList();
        var plan = new List<RewardLanding>(rewards.Length);

        foreach (var (itemId, count) in rewards)
        {
            if (itemId == 0 || count <= 0) continue;
            items.TryGetValue(itemId, out var template);
            int perStack = Math.Max(1, (int)(template?.Stack_Max ?? 1));

            if (perStack > 1)
            {
                int target = -1;
                for (int s = 0; s < stacks.Count; s++)
                {
                    var c = stacks[s];
                    if (c.Item.ItemId != itemId || c.Count + count > perStack) continue;
                    if (target < 0 || c.Item.Slot < stacks[target].Item.Slot) target = s;
                }
                if (target >= 0)
                {
                    int merged = stacks[target].Count + count;
                    stacks[target] = (stacks[target].Item, merged);
                    plan.Add(new RewardLanding(itemId, count, stacks[target].Item, merged, Array.Empty<InventoryItem>()));
                    continue;
                }
            }

            int needed = (count + perStack - 1) / perStack;
            if (free.Count - nextCell < needed)
                return null;

            short durability = template is null ? (short)-1 : (short)Math.Min(template.Durability, (uint)short.MaxValue);
            var opened = new List<InventoryItem>(needed);
            for (int remaining = count; remaining > 0;)
            {
                int n = Math.Min(perStack, remaining);
                var stack = new InventoryItem
                {
                    CharacterId = characterId,
                    ItemId = itemId,
                    Count = n,
                    Slot = free[nextCell++],
                    StorageType = 0, 
                    Durability = durability,
                };
                opened.Add(stack);
                stacks.Add((stack, n));
                remaining -= n;
            }
            plan.Add(new RewardLanding(itemId, count, null, 0, opened));
        }
        return plan;
    }
}
