using RO2.GameData;
using RO2.Quests.Application;
using RO2.Quests.Domain;
using Xunit;

namespace RO2.Tests.QuestCharacterization;

/// <summary>The complete decision picks the per-type counter; the give-up decision follows the host's held flag; each persists once.</summary>
public class QuestCompleteGiveUpUseCaseTests
{
    private sealed class Repo : IQuestRepository
    {
        public int Completes, GiveUps;
        public Task<IReadOnlyList<uint>> GetCompletedQuestIdsAsync(uint characterId) => throw new NotSupportedException();
        public Task<DateTime?> GetLastCompletedAtAsync(uint characterId, uint questId) => throw new NotSupportedException();
        public Task AcceptQuestAsync(uint characterId, uint questId) => throw new NotSupportedException();
        public Task<IReadOnlyList<CharacterQuest>> GetActiveQuestsAsync(uint characterId) => throw new NotSupportedException();
        public Task CompleteQuestAsync(uint characterId, uint questId) { Completes++; return Task.CompletedTask; }
        public Task GiveUpQuestAsync(uint characterId, uint questId) { GiveUps++; return Task.CompletedTask; }
        public Task SetQuestsTracedAsync(uint characterId, IReadOnlyCollection<uint> tracedQuestIds) => throw new NotSupportedException();
        public Task SaveProgressAsync(uint characterId, uint questId, string? progressJson) => throw new NotSupportedException();
    }

    private sealed class Data(byte type, uint value2, bool known = true) : IQuestGameData
    {
        public bool TryGetQuest(uint questId, out QuestData quest)
        { quest = new QuestData { Quest_ID = questId, Complete_Type = type, Complete_Type_Value2 = value2 }; return known; }
        public bool TryGetObjectiveRows(uint questId, out QuestData[] rows) { rows = []; return false; }
        public bool ItemExists(uint itemId) => false;
    }

    private const uint Q = 7;

    private static QuestCompleteResult Complete(byte type, uint value2, ushort progress, bool rewardAuto = false, bool held = true, bool known = true)
        => new QuestCompleteUseCase(new Repo(), new Data(type, value2, known)).Decide(new QuestCompleteRequest(Q, held, progress, rewardAuto));

    [Fact]
    public void UnknownThenNotHeld_StopBeforeAnyCounter()
    {
        Assert.Equal(QuestCompleteVerdict.UnknownQuest, Complete(1, 0, 0, held: false, known: false).Verdict);
        var notHeld = Complete(1, 0, 0, held: false);
        Assert.Equal((QuestCompleteVerdict.NotHeld, QuestCounterChange.None), (notHeld.Verdict, notHeld.Counter));
    }

    [Fact]
    public void Talk_FirstReportUpdatesAndClosesTo1_ARepeatOnlySetsTheCounter_RewardAutoPaysNow()
    {
        var first = Complete(1, 0, 0);
        Assert.Equal((true, true, false, QuestCounterChange.Set, (ushort)1), (first.SendUpdate, first.ClosesNow, first.PayNow, first.Counter, first.CounterValue));
        var again = Complete(1, 0, 1);
        Assert.Equal((false, false, QuestCounterChange.Set, (ushort)1), (again.SendUpdate, again.ClosesNow, again.Counter, again.CounterValue));
        var paid = Complete(1, 0, 0, rewardAuto: true);
        Assert.Equal((true, QuestCounterChange.Forget), (paid.PayNow, paid.Counter));
        Assert.False(Complete(4, 0, 0, rewardAuto: true).PayNow);
    }

    [Theory]
    [InlineData(300u, 7, 255, true)]
    [InlineData(0u, 0, 1, true)]
    [InlineData(3u, 3, 3, false)]
    [InlineData(300u, 400, 400, false)]
    [InlineData(255u, 255, 255, false)]
    public void Deliver_CounterIsMaxOfProgressAndByteCappedTotal(uint need, int progress, int counter, bool closesNow)
    {
        var r = Complete(5, need, (ushort)progress);
        Assert.Equal((QuestCounterChange.Set, (ushort)counter, closesNow, false), (r.Counter, r.CounterValue, r.ClosesNow, r.SendUpdate));
    }

    [Theory]
    [InlineData((byte)4)]
    [InlineData((byte)2)]
    [InlineData((byte)6)]
    public void OtherTypes_LeaveTheCounterAlone(byte type)
    {
        var r = Complete(type, 5, 2);
        Assert.Equal((QuestCompleteVerdict.Closed, QuestCounterChange.None, false), (r.Verdict, r.Counter, r.ClosesNow));
    }

    [Fact]
    public async Task CompletePersistsOnce()
    {
        var repo = new Repo();
        await new QuestCompleteUseCase(repo, new Data(1, 0)).CompleteAsync(1, Q);
        Assert.Equal(1, repo.Completes);
    }

    [Fact]
    public async Task GiveUp_RefusesWhenNotHeld_ElseCarriesTheQuestRow_AndPersistsOnce()
    {
        var repo = new Repo();
        var uc = new QuestGiveUpUseCase(repo, new Data(2, 3));
        var refused = uc.Decide(Q, wasHeld: false);
        Assert.Equal((QuestGiveUpVerdict.NotHeld, (QuestData?)null), (refused.Verdict, refused.Quest));
        var held = uc.Decide(Q, wasHeld: true);
        Assert.Equal((QuestGiveUpVerdict.GaveUp, Q), (held.Verdict, held.Quest!.Quest_ID));
        Assert.Null(new QuestGiveUpUseCase(repo, new Data(2, 3, known: false)).Decide(Q, true).Quest);
        Assert.Equal(0, repo.GiveUps);
        await uc.GiveUpAsync(1, Q);
        Assert.Equal(1, repo.GiveUps);
    }
}
