using RO2.GameData;
using RO2.GameData.Tables;
using RO2.Quests.Domain;

namespace RO2.Quests.Application;

/// <summary>A reward item the payout hands out: it exists in the item catalog and its count is at least 1.</summary>
public readonly record struct PrizeItem(uint ItemId, int Count);

/// <summary>The job change a quest pays: the target job id and its JobChange_Table row (null when the job has none).</summary>
public readonly record struct PrizeJobChange(uint JobId, JobChangeTable? Row);

/// <summary>What the host knows about the payout. <paramref name="QuestFrequency"/> is the Quest_Frequency column (0 = not repeatable).</summary>
public readonly record struct QuestPrizeRequest(uint CharacterId, QuestData Quest, uint RewardChoice, byte QuestFrequency);

/// <summary>
/// Everything the payout of a quest decides before it sends anything. The host runs its ordered payout over these values
/// and applies the exp rate itself: <see cref="DonationOf"/> takes the exp after that rate.
/// </summary>
/// <param name="Items">The reward items to pay, in slot order (unknown and empty slots dropped).</param>
/// <param name="NoValidPick">A choice quest with no valid pick: no item is paid and the host logs it.</param>
/// <param name="Money">Reward_Money, floored at 0.</param>
/// <param name="CompletionCount">The count the 2784 re-act reports; null for a non-repeatable quest (no 2784).</param>
/// <param name="JobChange">The job change the quest pays, or null when it pays none.</param>
/// <param name="DonationTarget">The guild the player donates the prize exp to, or null for a player with no guild.</param>
public sealed record PrizePlan(IReadOnlyList<PrizeItem> Items, bool NoValidPick, uint Money, int? CompletionCount,
    PrizeJobChange? JobChange, GuildDonationTarget? DonationTarget)
{
    /// <summary>The share of <paramref name="exp"/> that goes to the guild: floor(exp x percent / 100), 0 without a guild.</summary>
    public ulong DonationOf(ulong exp) =>
        exp == 0 || DonationTarget is not { } t ? 0 : RO2.Guilds.Domain.GuildProgression.HuntingBribeAmount(exp, t.Percent);
}

/// <summary>
/// The pure decisions of a quest payout (the 1378 turn-in and every other path into it): the cells its items need, and the
/// <see cref="PrizePlan"/> of what it pays. The payout sequence itself (item grants, 2784, 2429, donation, exp, level
/// quests, zeny, card, Khara, job change), the exp rate, the sends and the double-pay guard stay in the host.
/// </summary>
public sealed class QuestPrizeUseCase(IQuestRepository quests, IQuestGameData data, IGuildDonationView guilds)
{
    /// <summary>
    /// The bag cells the payout needs: the reward items that exist plus <paramref name="closeGrantItemIds"/> (the items the
    /// objective close hands out, of this quest and of every quest the turn-in closes) that exist. Runs before the quest is
    /// persisted as completed, so the host can refuse a full bag.
    /// </summary>
    public int CellsNeeded(QuestData quest, uint rewardChoice, IEnumerable<uint> closeGrantItemIds) =>
        QuestRewardRules.RewardItemsOf(quest, rewardChoice).Count(r => r.Id != 0 && data.ItemExists(r.Id))
        + closeGrantItemIds.Count(data.ItemExists);

    /// <summary>
    /// Composes the payout decisions. Call it after the quest is persisted as completed: the completion count reads that
    /// record.
    /// </summary>
    public async Task<PrizePlan> DecideAsync(QuestPrizeRequest request)
    {
        var quest = request.Quest;
        var items = new List<PrizeItem>();
        foreach (var (id, count) in QuestRewardRules.RewardItemsOf(quest, request.RewardChoice))
            if (id != 0 && data.ItemExists(id)) items.Add(new PrizeItem(id, (int)Math.Max(1, count)));

        int? completion = null;
        if (request.QuestFrequency > 0)
        {
            var completed = await quests.GetCompletedQuestIdsAsync(request.CharacterId);
            completion = QuestRewardRules.CompletionCount(completed.Count(id => id == quest.Quest_ID), request.QuestFrequency);
        }

        var rows = data.TryGetObjectiveRows(quest.Quest_ID, out var all) ? all : [quest];
        var jobRow = rows.FirstOrDefault(r => r.Action_Type == JobChangeSelection.JobChangeAction && r.Action_Value1 != 0);
        PrizeJobChange? jobChange = jobRow is null
            ? null
            : new PrizeJobChange(jobRow.Action_Value1, JobChangeSelection.Pick(jobRow.Action_Value1, quest.Quest_ID, data.JobChanges));

        return new PrizePlan(items, QuestRewardRules.NoValidPick(quest, request.RewardChoice), QuestRewardRules.MoneyOf(quest),
            completion, jobChange, guilds.DonationTargetOf(request.CharacterId));
    }
}
