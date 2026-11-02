using RO2.GameData;
using RO2.Quests.Application;
using RO2.Quests.Domain;
using Xunit;

namespace RO2.Tests.QuestCharacterization;

/// <summary>The accept decision runs unknown, active, one-shot, daily, class gate in that order and persists once on accept.</summary>
public class QuestAcceptUseCaseTests
{
    private sealed class Repo(DateTime? lastDone, params uint[] completed) : IQuestRepository
    {
        public int Accepts;
        public Task<IReadOnlyList<uint>> GetCompletedQuestIdsAsync(uint characterId) => Task.FromResult<IReadOnlyList<uint>>(completed);
        public Task<DateTime?> GetLastCompletedAtAsync(uint characterId, uint questId) => Task.FromResult(lastDone);
        public Task AcceptQuestAsync(uint characterId, uint questId) { Accepts++; return Task.CompletedTask; }
        public Task<IReadOnlyList<CharacterQuest>> GetActiveQuestsAsync(uint characterId) => throw new NotSupportedException();
        public Task CompleteQuestAsync(uint characterId, uint questId) => throw new NotSupportedException();
        public Task GiveUpQuestAsync(uint characterId, uint questId) => throw new NotSupportedException();
        public Task SetQuestsTracedAsync(uint characterId, IReadOnlyCollection<uint> tracedQuestIds) => throw new NotSupportedException();
        public Task SaveProgressAsync(uint characterId, uint questId, string? progressJson) => throw new NotSupportedException();
    }

    private sealed class Data(bool known) : IQuestGameData
    {
        public bool TryGetQuest(uint questId, out QuestData quest) { quest = new QuestData { Quest_ID = questId }; return known; }
        public bool TryGetObjectiveRows(uint questId, out QuestData[] rows) { rows = []; return false; }
        public bool ItemExists(uint itemId) => false;
        public IReadOnlyList<RO2.GameData.Tables.JobChangeTable> JobChanges => [];
    }

    private static readonly DateTime Now = new(2026, 11, 2, 15, 0, 0, DateTimeKind.Utc);
    private const uint Q = 7;

    private static QuestAcceptRequest Req(bool active = false, bool oneShot = false, byte daily = 0, QuestAcceptGateInput? gate = null)
        => new(1, Q, active, oneShot, daily, Now, gate);

    [Fact]
    public async Task EachGateStopsInOrder()
    {
        var badGate = new QuestAcceptGateInput(new AcceptGateColumns(0, 0, 50, 0, 0), 1, 0, 10, 0);
        var repo = new Repo(Now.AddHours(-1), Q);

        Assert.Equal(QuestAcceptVerdict.UnknownQuest, (await new QuestAcceptUseCase(repo, new Data(false)).DecideAsync(Req(active: true))).Verdict);
        var uc = new QuestAcceptUseCase(repo, new Data(true));
        Assert.Equal(QuestAcceptVerdict.AlreadyActive, (await uc.DecideAsync(Req(active: true, oneShot: true))).Verdict);
        Assert.Equal(QuestAcceptVerdict.OneShotDone, (await uc.DecideAsync(Req(oneShot: true, daily: 3))).Verdict);
        var daily = await uc.DecideAsync(Req(daily: 3, gate: badGate));
        Assert.Equal((QuestAcceptVerdict.DailyDone, (byte)3), (daily.Verdict, daily.DailyQuest));
        var refused = await uc.DecideAsync(Req(gate: badGate));
        Assert.Equal((QuestAcceptVerdict.GateRefused, QuestAcceptRules.ErrorJobMasterLevel), (refused.Verdict, refused.GateCode));
        Assert.Equal(QuestAcceptVerdict.Accepted, (await uc.DecideAsync(Req())).Verdict);
        Assert.Equal(0, repo.Accepts);
    }

    [Fact]
    public async Task AcceptPersistsOnce()
    {
        var repo = new Repo(null);
        await new QuestAcceptUseCase(repo, new Data(true)).AcceptAsync(1, Q);
        Assert.Equal(1, repo.Accepts);
    }
}
