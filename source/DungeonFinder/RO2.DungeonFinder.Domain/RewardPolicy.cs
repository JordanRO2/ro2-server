using RO2.GameData;
using RO2.GameData.Tables;

namespace RO2.DungeonFinder.Domain;

/// <summary>One reward item with its count.</summary>
public readonly record struct RewardItem(uint ItemId, int Count);

/// <summary>The 2816 completion state: the rewards are in the bag, or were mailed because the bag was full.</summary>
public enum CompleteGiftState : byte
{
    /// <summary>Rewards went to the bag.</summary>
    InBag = 2,

    /// <summary>Rewards were mailed.</summary>
    Mailed = 3,
}

/// <summary>The pure rules of a finder run's reward: which row, which items, how a mailed reward splits.</summary>
public static class RewardPolicy
{
    /// <summary>2333 pickup kind used for finder rewards. The client treats it like kind 0.</summary>
    public const ushort PickupKindFinderReward = 3;

    /// <summary>Mail kind of reward mails.</summary>
    public const byte SystemMailKind = 22;

    /// <summary>Mail sender, title and body string ids for reward mails.</summary>
    public const string MailSender = "11809", MailTitle = "11812", MailBody = "11813";

    /// <summary>
    /// The level that selects the reward row: the character's level plus its profession level. This server models no
    /// profession level, so the host passes 0 for it.
    /// </summary>
    public static int RewardLevel(int charLevel, int proJobLevel) => charLevel + proJobLevel;

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
    /// The filled item slots of a reward row, in slot order. Items missing from the item data are skipped and their
    /// ids returned in <c>skipped</c> for the caller to log.
    /// </summary>
    public static (List<RewardItem> items, List<uint> skipped) RewardItems(FindDungeonReward row,
        IReadOnlyDictionary<uint, ItemData> templates)
    {
        var items = new List<RewardItem>(3);
        var skipped = new List<uint>();
        foreach (var (id, count) in new[] { (row._1st_Item_ID, row._1st_Item_Num), (row._2nd_Item_ID, row._2nd_Item_Num), (row._3rd_Item_ID, row._3rd_Item_Num) })
        {
            if (id == 0 || count == 0) continue;
            if (!templates.ContainsKey(id))
            {
                skipped.Add(id);
                continue;
            }
            items.Add(new RewardItem(id, count));
        }
        return (items, skipped);
    }

    /// <summary>The attachment counts for <paramref name="count"/> items, at most <paramref name="perStack"/> each.</summary>
    public static List<int> SplitIntoMails(int count, int perStack)
    {
        perStack = Math.Max(1, perStack);
        var counts = new List<int>();
        for (int remaining = count; remaining > 0; remaining -= perStack)
            counts.Add(Math.Min(perStack, remaining));
        return counts;
    }
}
