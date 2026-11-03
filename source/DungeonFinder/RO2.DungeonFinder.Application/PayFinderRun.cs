using Microsoft.Extensions.Logging;
using RO2.BuildingBlocks;
using RO2.DungeonFinder.Domain;
using RO2.GameData;
using RO2.GameData.Tables;
using RO2.Inventory.Domain;
using RO2.Mails.Application;

namespace RO2.DungeonFinder.Application;

/// <summary>How paying a finder run ended.</summary>
public enum PayOutcome
{
    /// <summary>No Find_Dungeon_Reward row for the level: nothing paid, no 2816.</summary>
    NoRow,

    /// <summary>The bag was full and no reward mail could be stored: nothing paid, no 2816.</summary>
    NotPaid,

    /// <summary>The bag was full; the items were mailed, the money credited, the 2816 sent in state Mailed.</summary>
    Mailed,

    /// <summary>The items went to the bag, the money was credited, the 2816 sent in state InBag.</summary>
    Paid,
}

/// <summary>What <see cref="PayFinderRun"/> did, for the host to log.</summary>
/// <param name="Outcome">How the payment ended.</param>
/// <param name="Level">The reward level used to pick the row.</param>
/// <param name="Row">The reward row, or null for <see cref="PayOutcome.NoRow"/>.</param>
/// <param name="Items">How many reward item slots were filled.</param>
/// <param name="Mails">How many reward mails were stored (mail path).</param>
/// <param name="SkippedItems">Reward item ids missing from the item data, skipped.</param>
public sealed record PayFinderRunResult(PayOutcome Outcome, int Level, FindDungeonReward? Row, int Items, int Mails,
    IReadOnlyList<uint> SkippedItems);

/// <summary>Placement of one reward item: merged into a held stack, or into new stacks.</summary>
public sealed record Landing(uint ItemId, int Count, InventoryItem? MergeInto, int MergedCount, IReadOnlyList<InventoryItem> NewStacks);

/// <summary>
/// Pays a finder run that the host has already ended (end-before-pay: a failure here forfeits the reward). Items go to the
/// bag when they all fit whole, else they are mailed; the money is credited after the items; the 2816 comes last.
/// Job mastery points (<c>Exp</c>) are never granted, the server does not track them.
/// </summary>
public sealed class PayFinderRun(IInventoryRepository inventory, ISendMail mails, IFinderRewardPublisher publisher,
    ILogger<PayFinderRun> logger)
{
    /// <summary>Pays <paramref name="characterId"/>'s finished run of <paramref name="list"/>, crediting <paramref name="wallet"/>.</summary>
    public async Task<PayFinderRunResult> PayAsync(uint characterId, string name, int charLevel, int proJobLevel,
        FindDungeonList list, IWallet wallet, IReadOnlyList<FindDungeonCategory> categories,
        IReadOnlyList<FindDungeonReward> rewards, IReadOnlyDictionary<uint, ItemData> templates)
    {
        int level = RewardPolicy.RewardLevel(charLevel, proJobLevel);
        var row = RewardPolicy.RewardRow(categories, rewards, list, level);
        if (row is null) return new PayFinderRunResult(PayOutcome.NoRow, level, null, 0, 0, []);

        var (items, skipped) = RewardPolicy.RewardItems(row, templates);
        var owned = await inventory.GetByCharacterAsync(characterId);
        var plan = PlanBag(items, owned, characterId, templates);
        if (plan is null)
        {
            int mailed = await MailRewardAsync(characterId, name, items, templates);
            if (mailed == 0) return new PayFinderRunResult(PayOutcome.NotPaid, level, row, items.Count, 0, skipped);
            CreditMoney(wallet, row.Money);
            await publisher.Completed(characterId, list.List_ID, CompleteGiftState.Mailed);
            return new PayFinderRunResult(PayOutcome.Mailed, level, row, items.Count, mailed, skipped);
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
                logger.LogWarning(ex, "{Name}: finder reward {Item} x{Count} could not be saved", name, landing.ItemId, landing.Count);
                if (landed.Count == 0) continue;
            }

            await publisher.ItemLanded(characterId, landing.ItemId, landing.Count, landed);
        }

        CreditMoney(wallet, row.Money);
        await publisher.Completed(characterId, list.List_ID, CompleteGiftState.InBag);
        return new PayFinderRunResult(PayOutcome.Paid, level, row, items.Count, 0, skipped);
    }

    /// <summary>Credits the reward money to the player. The next character update shows the new balance.</summary>
    private static void CreditMoney(IWallet wallet, long money)
    {
        if (money <= 0) return;
        wallet.Credit(money);
    }

    /// <summary>
    /// Mails <paramref name="items"/> as system mails, one per stack of at most the template stack maximum. Returns
    /// how many mails were stored; a mail that fails is logged and skipped.
    /// </summary>
    private async Task<int> MailRewardAsync(uint characterId, string name, IReadOnlyList<RewardItem> items,
        IReadOnlyDictionary<uint, ItemData> templates)
    {
        int stored = 0;
        foreach (var (itemId, count) in items)
        {
            templates.TryGetValue(itemId, out var template);
            int perStack = Math.Max(1, (int)(template?.Stack_Max ?? 1));
            short durability = template is null ? (short)-1 : (short)Math.Min(template.Durability, (uint)short.MaxValue);
            foreach (var n in RewardPolicy.SplitIntoMails(count, perStack))
            {
                try
                {
                    await mails.SendSystemMailAsync(new MailDraft
                    {
                        ReceiverCharId = characterId,
                        ReceiverName = name,
                        SenderName = RewardPolicy.MailSender,
                        Title = RewardPolicy.MailTitle,
                        Body = RewardPolicy.MailBody,
                        Kind = RewardPolicy.SystemMailKind,
                        SentAt = DateTime.UtcNow,
                        Item = new ItemStackSnapshot
                        {
                            Id = await inventory.GetNextIdAsync(),
                            ItemId = itemId,
                            Count = n,
                            Durability = durability,
                        },
                    });
                    stored++;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "{Name}: finder reward mail of {Item} x{Count} could not be stored", name, itemId, n);
                }
            }
        }
        return stored;
    }

    /// <summary>
    /// Plans how <paramref name="items"/> fit into the bag <paramref name="owned"/> without writing anything. An item
    /// that fits whole into a held stack of its template is merged into the lowest such stack; the rest open new
    /// stacks in the free cells. Returns null when the bag cannot hold every item.
    /// </summary>
    public static List<Landing>? PlanBag(IReadOnlyList<RewardItem> items, IReadOnlyList<InventoryItem> owned,
        uint characterId, IReadOnlyDictionary<uint, ItemData> templates)
    {
        // Same cell math as the host BagCapacity.FreeCells (the Zone host, which Application cannot reference).
        var free = BagLayout.FreeCells(
            owned.Where(i => i.StorageType == 0 && i.Slot >= 0).Select(i => (int)i.Slot),
            owned.Where(i => i.StorageType == 3 && i.Slot >= 0)
                 .Select(i => new ExtraBag(i.Slot, templates.TryGetValue(i.ItemId, out var data) ? data.Bag_Size : 0))
                 .ToList());
        var bagRows = owned.Where(i => i.StorageType == 0 && i.Slot >= 0 && i.Count > 0).ToList();
        var entries = new List<(uint, int, byte)>(items.Count);
        foreach (var (itemId, count) in items)
            entries.Add((itemId, count, templates.TryGetValue(itemId, out var t) ? t.Stack_Max : (byte)1));

        // The sequential placement rule is the shared Inventory.Domain primitive; this maps its result onto Landing
        // rows and keeps the all-or-nothing policy (any refused entry => null => the caller mails the reward).
        var seq = BagStackPlanner.PlanSequence(
            bagRows.Select(i => new BagStack(i.Id, i.ItemId, i.Count, i.Slot)).ToList(), free, entries);

        var plan = new List<Landing>(items.Count);
        var bySlot = new Dictionary<short, InventoryItem>();
        foreach (var i in bagRows) bySlot[i.Slot] = i;
        for (int k = 0; k < items.Count; k++)
        {
            var (itemId, count) = items[k];
            var pl = seq[k];
            if (pl is null) continue;
            if (pl.Refused) return null;
            if (pl.MergeIntoSlot is { } slot)
            {
                plan.Add(new Landing(itemId, count, bySlot[slot], pl.MergedCount, []));
                continue;
            }
            templates.TryGetValue(itemId, out var template);
            short durability = template is null ? (short)-1 : (short)Math.Min(template.Durability, (uint)short.MaxValue);
            var opened = new List<InventoryItem>(pl.Opened.Count);
            foreach (var (cell, n) in pl.Opened)
            {
                var stack = new InventoryItem
                {
                    CharacterId = characterId,
                    ItemId = itemId,
                    Count = n,
                    Slot = cell,
                    StorageType = 0,
                    Durability = durability,
                };
                opened.Add(stack);
                bySlot[cell] = stack;   // an opened stack is a merge target for a later reward row
            }
            plan.Add(new Landing(itemId, count, null, 0, opened));
        }
        return plan;
    }
}
