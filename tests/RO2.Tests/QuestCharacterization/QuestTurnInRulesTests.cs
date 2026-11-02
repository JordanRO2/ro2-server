using RO2.GameData;
using RO2.GameData.Tables;
using RO2.Quests.Domain;
using Xunit;

namespace RO2.Tests.QuestCharacterization;

/// <summary>B8 S13: unit tests of the Domain turn-in, reward and job-change rules (no store, no host).</summary>
public class QuestTurnInRulesTests
{
    private static QuestData Talk(uint npc, uint rewardNpc) => Q.Row(1, npc, rewardNpc: rewardNpc);

    [Fact]
    public void TalkMetElsewhereUnmet_TrueCase_AndEachConjunctShortCircuits()
    {
        var calls = 0;
        Func<uint, bool> meetable = n => { calls++; return n == 10; };
        var quest = Talk(10, rewardNpc: 11);

        Assert.True(QuestTurnInRules.TalkMetElsewhereUnmet(quest, 12, objectiveClosed: false, acceptedThisLogin: true, meetable));
        Assert.Equal(1, calls);

        calls = 0;
        Assert.False(QuestTurnInRules.TalkMetElsewhereUnmet(quest, 12, true, true, meetable));          // closed
        Assert.False(QuestTurnInRules.TalkMetElsewhereUnmet(quest, 12, false, false, meetable));        // not accepted this login
        Assert.False(QuestTurnInRules.TalkMetElsewhereUnmet(quest, 10, false, true, meetable));         // giver is the talk NPC
        Assert.False(QuestTurnInRules.TalkMetElsewhereUnmet(Talk(10, 10), 12, false, true, meetable));  // talk NPC is the reward NPC
        Assert.False(QuestTurnInRules.TalkMetElsewhereUnmet(Talk(0, 11), 12, false, true, meetable));   // no talk NPC
        Assert.False(QuestTurnInRules.TalkMetElsewhereUnmet(Q.Row(2, 10), 12, false, true, meetable));  // not a talk row
        Assert.Equal(0, calls);                                                                          // the lookup is last

        Assert.False(QuestTurnInRules.TalkMetElsewhereUnmet(Talk(13, 11), 12, false, true, meetable));  // NPC not meetable
        Assert.Equal(1, calls);
    }

    [Fact]
    public void ClosesWithTurnIn_NeedsSingleTalkAtGiver_NoProgress_NotFailed()
    {
        QuestData[] rows = [Q.Row(1, 10), Q.Row(0)];
        Assert.True(QuestTurnInRules.ClosesWithTurnIn(rows, 10, 0, false));
        Assert.False(QuestTurnInRules.ClosesWithTurnIn(rows, 11, 0, false));
        Assert.False(QuestTurnInRules.ClosesWithTurnIn(rows, 10, 1, false));
        Assert.False(QuestTurnInRules.ClosesWithTurnIn(rows, 10, 0, true));
        Assert.False(QuestTurnInRules.ClosesWithTurnIn([Q.Row(1, 10), Q.Row(1, 10)], 10, 0, false));
    }

    [Fact]
    public void StacksEmptiedBy_EmptiesWholeStacksInBagOrder()
    {
        var bag = new (ulong, uint, int)[] { (1, 5, 3), (2, 9, 4), (3, 5, 4), (4, 5, 0) };
        Assert.Equal(new ulong[] { 1 }, QuestTurnInRules.StacksEmptiedBy(bag, 5, 3).ToArray());
        Assert.Equal(new ulong[] { 1, 3 }, QuestTurnInRules.StacksEmptiedBy(bag, 5, 7).Order().ToArray());
        Assert.Empty(QuestTurnInRules.StacksEmptiedBy(bag, 5, 2));
    }

    [Fact]
    public void RewardItemsOf_ByRewardType_AndMoneyFloor()
    {
        var q = new QuestData { Reward_Type = 1, Reward_Item_ID1 = 7, Reward_Item_Count1 = 2, Reward_Item_ID2 = 8, Reward_Item_Count2 = 3, Reward_Money = -5 };
        Assert.Equal(q.RewardItemSlots(), QuestRewardRules.RewardItemsOf(q, 99));
        foreach (byte t in new byte[] { 2, 5 })
        {
            var p = q with { Reward_Type = t };
            Assert.Equal([(8u, 3u)], QuestRewardRules.RewardItemsOf(p, 1));
            Assert.Empty(QuestRewardRules.RewardItemsOf(p, (uint)q.RewardItemSlots().Length));
        }
        Assert.Empty(QuestRewardRules.RewardItemsOf(q with { Reward_Type = 3 }, 0));
        Assert.Equal(0u, QuestRewardRules.MoneyOf(q));
        Assert.Equal(40u, QuestRewardRules.MoneyOf(q with { Reward_Money = 40 }));
    }

    [Fact]
    public void JobChangePick_QuestRow_ThenNonMaster_ThenAnyRow_AndNullAboveByte()
    {
        var master = new JobChangeTable { Job_ID = 3, ISJobMaster = 1, Need_Quest_ID = 0 };
        var plain = new JobChangeTable { Job_ID = 3, ISJobMaster = 0, Need_Quest_ID = 0 };
        var forQuest = new JobChangeTable { Job_ID = 3, ISJobMaster = 1, Need_Quest_ID = 50 };
        Assert.Same(forQuest, JobChangeSelection.Pick(3, 50, [master, plain, forQuest]));
        Assert.Same(plain, JobChangeSelection.Pick(3, 51, [master, plain, forQuest]));
        Assert.Same(master, JobChangeSelection.Pick(3, 51, [master, forQuest]));
        Assert.Null(JobChangeSelection.Pick(4, 50, [master, plain]));
        Assert.Null(JobChangeSelection.Pick(256, 50, [master]));
    }
}
