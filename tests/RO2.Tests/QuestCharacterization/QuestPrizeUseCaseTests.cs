using RO2.GameData;
using RO2.GameData.Tables;
using RO2.Guilds.Domain;
using RO2.Quests.Application;
using RO2.Quests.Domain;
using Xunit;

namespace RO2.Tests.QuestCharacterization;

/// <summary>The payout decisions: the completion-count clamp, the choice check, the cells needed and the plan the host consumes.</summary>
public class QuestPrizeUseCaseTests
{
    private const uint Q = 7;

    private sealed class Repo(int completions) : IQuestRepository
    {
        public int Reads;
        public Task<IReadOnlyList<uint>> GetCompletedQuestIdsAsync(uint characterId)
        { Reads++; return Task.FromResult<IReadOnlyList<uint>>(Enumerable.Repeat(Q, completions).Append(99u).ToArray()); }
        public Task<DateTime?> GetLastCompletedAtAsync(uint characterId, uint questId) => throw new NotSupportedException();
        public Task AcceptQuestAsync(uint characterId, uint questId) => throw new NotSupportedException();
        public Task<IReadOnlyList<CharacterQuest>> GetActiveQuestsAsync(uint characterId) => throw new NotSupportedException();
        public Task CompleteQuestAsync(uint characterId, uint questId) => throw new NotSupportedException();
        public Task GiveUpQuestAsync(uint characterId, uint questId) => throw new NotSupportedException();
        public Task SetQuestsTracedAsync(uint characterId, IReadOnlyCollection<uint> tracedQuestIds) => throw new NotSupportedException();
        public Task SaveProgressAsync(uint characterId, uint questId, string? progressJson) => throw new NotSupportedException();
    }

    private sealed class Data(QuestData[] rows, params uint[] items) : IQuestGameData
    {
        public bool TryGetQuest(uint questId, out QuestData quest) { quest = rows[0]; return true; }
        public bool TryGetObjectiveRows(uint questId, out QuestData[] r) { r = rows; return rows.Length > 1; }
        public bool ItemExists(uint itemId) => items.Contains(itemId);
        public IReadOnlyList<JobChangeTable> JobChanges { get; } =
            [new JobChangeTable { Job_ID = 5, Need_Quest_ID = 0, ISJobMaster = 1 }, new JobChangeTable { Job_ID = 5, Need_Quest_ID = Q, ISJobMaster = 1 }];
    }

    private sealed class Guilds(GuildDonationTarget? target) : IGuildDonationView
    {
        public GuildDonationTarget? DonationTargetOf(uint charId) => target;
    }

    private static QuestData Quest(byte rewardType = 1, int money = 0) => new()
    {
        Quest_ID = Q, Reward_Type = rewardType, Reward_Money = money,
        Reward_Item_ID1 = 11, Reward_Item_Count1 = 0, Reward_Item_ID2 = 12, Reward_Item_Count2 = 4, Reward_Item_ID3 = 13, Reward_Item_Count3 = 1,
    };

    [Theory]
    [InlineData(0, 3, 1)]
    [InlineData(1, 3, 1)]
    [InlineData(2, 3, 2)]
    [InlineData(9, 3, 3)]
    [InlineData(5, 0, 1)]
    public void CompletionCount_ClampsToOneThroughTheFrequency(int completed, byte frequency, int expected)
        => Assert.Equal(expected, QuestRewardRules.CompletionCount(completed, frequency));

    [Theory]
    [InlineData((byte)1, 0u, false)]
    [InlineData((byte)2, 1u, false)]
    [InlineData((byte)2, 3u, true)]
    [InlineData((byte)5, 0xFFFFFFFFu, true)]
    [InlineData((byte)0, 0xFFFFFFFFu, false)]
    public void NoValidPick_OnlyAChoiceQuestWithAnEmptyOrPastSlot(byte type, uint choice, bool expected)
        => Assert.Equal(expected, QuestRewardRules.NoValidPick(Quest(type), choice));

    [Fact]
    public void CellsNeeded_CountsExistingRewardItemsAndExistingCloseGrants()
    {
        var uc = new QuestPrizeUseCase(new Repo(0), new Data([Quest()], 11, 12), new Guilds(null));
        Assert.Equal(2 + 1, uc.CellsNeeded(Quest(), 0xFFFFFFFF, [12, 500]));
        Assert.Equal(1, uc.CellsNeeded(Quest(2), 1, []));
    }

    [Fact]
    public async Task Plan_ComposesItemsMoneyCompletionJobChangeAndDonation()
    {
        var job = Quest(1, money: 250) with { Action_Type = JobChangeSelection.JobChangeAction, Action_Value1 = 5 };
        var repo = new Repo(2);
        var guild = new Guild();
        var plan = await new QuestPrizeUseCase(repo, new Data([job, Quest()], 11, 12), new Guilds(new GuildDonationTarget(guild, 30)))
            .DecideAsync(new QuestPrizeRequest(1, job, 0xFFFFFFFF, QuestFrequency: 3));

        Assert.Equal([new PrizeItem(11, 1), new PrizeItem(12, 4)], plan.Items);
        Assert.Equal((250u, 2, false), (plan.Money, plan.CompletionCount, plan.NoValidPick));
        Assert.Equal(5u, plan.JobChange!.Value.JobId);
        Assert.Equal(Q, plan.JobChange.Value.Row!.Need_Quest_ID);
        Assert.Same(guild, plan.DonationTarget!.Value.Guild);
        Assert.Equal(30ul, plan.DonationOf(101));
    }

    [Fact]
    public async Task Plan_NonRepeatableNoGuildNoJobChange_ReadsNothing()
    {
        var repo = new Repo(2);
        var plan = await new QuestPrizeUseCase(repo, new Data([Quest(2)], 12), new Guilds(null))
            .DecideAsync(new QuestPrizeRequest(1, Quest(2), 5, QuestFrequency: 0));

        Assert.Empty(plan.Items);
        Assert.True(plan.NoValidPick);
        Assert.Null(plan.CompletionCount);
        Assert.Null(plan.JobChange);
        Assert.Equal((0, 0ul), (repo.Reads, plan.DonationOf(1000)));
    }
}
