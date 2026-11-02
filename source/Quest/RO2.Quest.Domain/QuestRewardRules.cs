using RO2.GameData;

namespace RO2.Quests.Domain;

/// <summary>
/// Pure reward-selection rules of a Quest_Info row: which reward slots a Reward_Type pays and the money floor. The payout
/// sequence, the exp rate and the wire stay in the host.
/// </summary>
public static class QuestRewardRules
{
    /// <summary>Quest_Info <c>Reward_Type</c> that pays every filled reward slot.</summary>
    public const byte RewardTypeAllSlots = 1;

    /// <summary>Quest_Info <c>Reward_Type</c> that pays the one slot named by the reward choice.</summary>
    public const byte RewardTypePickOne = 2;

    /// <summary>Quest_Info <c>Reward_Type</c> that pays the one slot named by the reward choice, like <see cref="RewardTypePickOne"/>.</summary>
    public const byte RewardTypePickOneAlt = 5;

    /// <summary>
    /// The reward items of a quest for the given reward choice, by reward type. Empty slots are included and must be skipped
    /// by the caller. Type 1: every slot; types 2 and 5: the slot at the 0-based choice (none when past the slots); any
    /// other type: none.
    /// </summary>
    public static (uint Id, uint Count)[] RewardItemsOf(QuestData quest, uint rewardChoice)
    {
        var slots = quest.RewardItemSlots();
        return quest.Reward_Type switch
        {
            RewardTypeAllSlots => slots,
            RewardTypePickOne or RewardTypePickOneAlt => rewardChoice < (uint)slots.Length ? new[] { slots[rewardChoice] } : Array.Empty<(uint Id, uint Count)>(),
            _ => Array.Empty<(uint Id, uint Count)>(),
        };
    }

    /// <summary>The money a quest pays: Reward_Money, floored at 0.</summary>
    public static uint MoneyOf(QuestData quest) => (uint)Math.Max(0, quest.Reward_Money);

    /// <summary>
    /// True when a choice quest (types 2 and 5) pays no item: the choice is past the slots or names an empty slot. Other
    /// reward types never have a choice to get wrong.
    /// </summary>
    public static bool NoValidPick(QuestData quest, uint rewardChoice)
    {
        var slots = quest.RewardItemSlots();
        return quest.Reward_Type is RewardTypePickOne or RewardTypePickOneAlt
               && (rewardChoice >= (uint)slots.Length || slots[rewardChoice].Id == 0);
    }

    /// <summary>
    /// The completion count the 2784 re-act reports: how many times the character has completed the quest, clamped to
    /// 1..<paramref name="questFrequency"/> (the Quest_Info Quest_Frequency column). Only a repeatable quest (frequency
    /// above 0) reports one; a frequency of 0 is read as 1.
    /// </summary>
    public static int CompletionCount(int completedCount, byte questFrequency) =>
        Math.Clamp(completedCount, 1, Math.Max(1, (int)questFrequency));
}
