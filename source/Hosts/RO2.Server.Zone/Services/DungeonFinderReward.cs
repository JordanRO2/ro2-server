using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.GameData;
using RO2.GameData.Tables;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Inventory.Domain;
using RO2.Characters.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Pays a dungeon finder run when its list's last boss dies: the 2816 completion notice and the reward items,
/// money and mail that go with it. Called right after the boss's corpse event.
/// </summary>
/// <remarks>
/// <para>
/// Only the player who started the run is paid, and only once. The run is the one recorded by
/// <see cref="DungeonFinderPendingStore"/> when the finder entry was made.
/// </para>
/// <para>
/// Items are added to the bag first, then the money is credited, then the 2816 is sent. Reward rows are chosen
/// by the character's level from Find_Dungeon_Reward.
/// </para>
/// <para>
/// Job mastery points from the reward are not granted, because the server does not track them.
/// </para>
/// <para>
/// Rewards that do not fit in the bag are mailed as system mails. In that case the 2816 state is 3. If no mail
/// can be stored, nothing is paid and no 2816 is sent. The run ends in either case.
/// </para>
/// </remarks>
public static class DungeonFinderReward
{
 /// 2333 pickup kind used for finder rewards. The client treats it like kind 0.
    public const ushort PickupKindFinderReward = 3;

 /// One reward item with its count.
    public readonly record struct RewardItem(uint ItemId, int Count);

 /// Placement of one reward item: merged into a held stack, or into new stacks.
    public sealed record Landing(uint ItemId, int Count, InventoryItem? MergeInto, int MergedCount, IReadOnlyList<InventoryItem> NewStacks);

 /// <summary>
 /// Pays <paramref name="killer"/>'s finder run when <paramref name="npcId"/> is the list's last boss.
 /// </summary>
    public static async Task TryPayAsync(ProudSession session, PlayerState killer, uint npcId, GameDataStore data,
        LootService loot, IServiceProvider services, ILogger logger)
    {
        var run = DungeonFinderPendingStore.GetRun(killer);
        if (run is null) return;
        var list = data.FindDungeonList.FirstOrDefault(r => r.List_ID == run.ListId);
        if (list is null || list.Last_Boss_ID != npcId) return;
        if (killer.MapId != run.ArrivalMapId)
        {
            logger.LogInformation("{Name} killed finder boss {Npc} of list {List} on map {Map}, not the run's map {Arrival}: not paid",
                killer.Name, npcId, run.ListId, killer.MapId, run.ArrivalMapId);
            return;
        }
        if (!DungeonFinderPendingStore.TryEndRun(killer, run)) return;   

        int level = RewardLevel(killer);
        var row = RewardRow(data.FindDungeonCategory, data.FindDungeonReward, list, level);
        if (row is null)
        {
            logger.LogWarning("{Name} finished finder list {List}: no Find_Dungeon_Reward row for level {Level}, no 2816",
                killer.Name, run.ListId, level);
            return;
        }

        var items = RewardItems(row, data.Items, killer.Name, logger);
        var inventory = services.GetRequiredService<IInventoryRepository>();
        var owned = await inventory.GetByCharacterAsync(killer.CharacterId);
        var plan = PlanBag(items, owned, killer.CharacterId, data.Items);
        if (plan is null)
        {
            
            int mailed = await MailRewardAsync(killer, items, data.Items, inventory, services, logger);
            if (mailed == 0)
            {
                logger.LogWarning("{Name} finished finder list {List} with no bag room for reward row {Row}, and no reward mail could be stored: not paid, no 2816",
                    killer.Name, run.ListId, row.ID);
                return;
            }
            CreditMoney(killer, row.Money);
            await session.SendRmiAsync(ZoneS2COpcodes.NfyRandomDungeonCompleteGift,
                DungeonFinderPackets.BuildCompleteGift2816(run.ListId, DungeonFinderPackets.CompleteGiftMailed));
            logger.LogInformation("{Name} finished finder list {List} with a full bag: reward row {Row} mailed ({Mails} mails, {Money} zeny credited; {Exp} job-mastery points not modelled)",
                killer.Name, run.ListId, row.ID, mailed, row.Money, row.Exp);
            return;
        }

        foreach (var landing in plan)
        {
            var landed = new List<InventoryItem>(1);
            try
            {
                if (landing.MergeInto is { } held)
                {
                    held.Count = landing.MergedCount;
                    await inventory.UpdateItemAsync(held);
                    landed.Add(held);
                }
                foreach (var stack in landing.NewStacks)
                {
                    await inventory.AddItemAsync(stack);   
                    landed.Add(stack);
                }
            }
            catch (Exception ex)
            {
                
                logger.LogWarning(ex, "{Name}: finder reward {Item} x{Count} could not be saved", killer.Name, landing.ItemId, landing.Count);
                if (landed.Count == 0) continue;
            }

            
            
            await loot.AdvanceCollectQuestsAsync(session, killer, landing.ItemId, landing.Count);
            await KharaService.OnHeldItemChangedAsync(session, killer, landing.ItemId, services, logger);
            foreach (var stack in landed)
                await session.SendRmiAsync(ZoneS2COpcodes.OnPickupItem,
                    LootNotifyPackets.BuildPickupItem2333(WorldEntryService.BuildItemRecord126(stack),
                        pickupKind: PickupKindFinderReward));
        }

        CreditMoney(killer, row.Money);

        await session.SendRmiAsync(ZoneS2COpcodes.NfyRandomDungeonCompleteGift, DungeonFinderPackets.BuildCompleteGift2816(run.ListId));
        logger.LogInformation("{Name} finished finder list {List}: reward row {Row} ({Items} items, {Money} zeny; {Exp} job-mastery points not modelled)",
            killer.Name, run.ListId, row.ID, items.Count, row.Money, row.Exp);
    }

 /// Mail kind of reward mails.
    private const byte SystemMailKind = 22;

 /// Mail sender, title and body string ids for reward mails.
    internal const string MailSender = "11809", MailTitle = "11812", MailBody = "11813";

 /// <summary>Credits the reward money to the player. The next character update shows the new balance.</summary>
    private static void CreditMoney(PlayerState killer, long money)
    {
        if (money <= 0) return;
        killer.Credit(money);
    }

 /// <summary>
 /// Mails <paramref name="items"/> as system mails, one per stack of at most the template's stack maximum. Returns
 /// how many mails were stored; a mail that fails is logged and skipped.
 /// </summary>
    private static async Task<int> MailRewardAsync(PlayerState killer, IReadOnlyList<RewardItem> items,
        IReadOnlyDictionary<uint, ItemData> templates, IInventoryRepository inventory, IServiceProvider services,
        ILogger logger)
    {
        var mails = services.GetRequiredService<IMailRepository>();
        int stored = 0;
        foreach (var (itemId, count) in items)
        {
            templates.TryGetValue(itemId, out var template);
            int perStack = Math.Max(1, (int)(template?.Stack_Max ?? 1));
            short durability = template is null ? (short)-1 : (short)Math.Min(template.Durability, (uint)short.MaxValue);
            foreach (var n in SplitIntoMails(count, perStack))
            {
                try
                {
                    await mails.AddAsync(new MailMessage
                    {
                        ReceiverCharId = killer.CharacterId,
                        ReceiverName = killer.Name,
                        SenderName = MailSender,
                        Title = MailTitle,
                        Body = MailBody,
                        Kind = SystemMailKind,
                        SentAt = DateTime.UtcNow,
                        AttachedItemUid = await inventory.GetNextIdAsync(),
                        AttachedItemId = itemId,
                        AttachedItemCount = n,
                        AttachedItemDurability = durability,
                    });
                    stored++;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "{Name}: finder reward mail of {Item} x{Count} could not be stored", killer.Name, itemId, n);
                }
            }
        }
        return stored;
    }

 /// <summary>The attachment counts for <paramref name="count"/> items, at most <paramref name="perStack"/> each.</summary>
    internal static List<int> SplitIntoMails(int count, int perStack)
    {
        perStack = Math.Max(1, perStack);
        var counts = new List<int>();
        for (int remaining = count; remaining > 0; remaining -= perStack)
            counts.Add(Math.Min(perStack, remaining));
        return counts;
    }

 /// <summary>
 /// The level that selects the reward row: the character's level plus its profession level. This server models no
 /// profession level, so it is the character's level.
 /// </summary>
    public static int RewardLevel(PlayerState player)
        => player.Level + (player.Character is { } c ? CharacterSerializer.WireProJob(c).Level : 0);

 /// <summary>The Find_Dungeon_Reward row of <paramref name="list"/> for <paramref name="level"/>, or null.</summary>
    public static FindDungeonReward? RewardRow(IReadOnlyList<FindDungeonCategory> categories,
        IReadOnlyList<FindDungeonReward> rewards, FindDungeonList list, int level)
    {
        byte group = list.Reward_GroupID;
        if (group == 0)
            group = categories.FirstOrDefault(c => c.Category_ID == list.Category_ID)?.Reward_GroupID ?? 0;
        if (group == 0) return null;
        return rewards.FirstOrDefault(r => r.Reward_GroupID == group && r.MinLevel <= level && level <= r.MaxLevel);
    }

 /// <summary>
 /// The filled item slots of a reward row, in slot order. Items missing from the item data are skipped.
 /// </summary>
    public static List<RewardItem> RewardItems(FindDungeonReward row, IReadOnlyDictionary<uint, ItemData> templates,
        string playerName, ILogger logger)
    {
        var items = new List<RewardItem>(3);
        foreach (var (id, count) in new[] { (row._1st_Item_ID, row._1st_Item_Num), (row._2nd_Item_ID, row._2nd_Item_Num), (row._3rd_Item_ID, row._3rd_Item_Num) })
        {
            if (id == 0 || count == 0) continue;
            if (!templates.ContainsKey(id))
            {
                logger.LogWarning("{Name}: finder reward row {Row} names item {Item}, which is not in ItemInfo; skipped", playerName, row.ID, id);
                continue;
            }
            items.Add(new RewardItem(id, count));
        }
        return items;
    }

 /// <summary>
 /// Plans how <paramref name="items"/> fit into the bag <paramref name="owned"/> without writing anything. An item
 /// that fits whole into a held stack of its template is merged into the lowest such stack; the rest open new
 /// stacks in the free cells. Returns null when the bag cannot hold every item.
 /// </summary>
    public static List<Landing>? PlanBag(IReadOnlyList<RewardItem> items, IReadOnlyList<InventoryItem> owned,
        uint characterId, IReadOnlyDictionary<uint, ItemData> templates)
    {
        var plan = new List<Landing>(items.Count);
        var free = BagCapacity.FreeCells(owned, templates);
        int nextCell = 0;
        var stacks = owned.Where(i => i.StorageType == 0 && i.Slot >= 0 && i.Count > 0)
            .Select(i => (Item: i, Count: i.Count)).ToList();

        foreach (var (itemId, count) in items)
        {
            templates.TryGetValue(itemId, out var template);
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
                    plan.Add(new Landing(itemId, count, stacks[target].Item, merged, []));
                    continue;
                }
            }

            int needed = (count + perStack - 1) / perStack;
            if (free.Count - nextCell < needed) return null;
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
            plan.Add(new Landing(itemId, count, null, 0, opened));
        }
        return plan;
    }
}
