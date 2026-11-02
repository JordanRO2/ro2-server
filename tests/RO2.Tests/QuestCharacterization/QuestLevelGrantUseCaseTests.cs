using RO2.GameData;
using RO2.Quests.Application;
using RO2.Quests.Domain;
using Xunit;

namespace RO2.Tests.QuestCharacterization;

/// <summary>The level-grant decision accepts each due quest not completed or held, in id order, then calls back; it reads nothing when none is due.</summary>
public class QuestLevelGrantUseCaseTests
{
    private sealed class Repo(params uint[] completed) : IQuestRepository
    {
        public readonly List<uint> Accepted = [];
        public int CompletedReads;
        public Task<IReadOnlyList<uint>> GetCompletedQuestIdsAsync(uint characterId) { CompletedReads++; return Task.FromResult<IReadOnlyList<uint>>(completed); }
        public Task<DateTime?> GetLastCompletedAtAsync(uint characterId, uint questId) => throw new NotSupportedException();
        public Task AcceptQuestAsync(uint characterId, uint questId) { Accepted.Add(questId); return Task.CompletedTask; }
        public Task<IReadOnlyList<CharacterQuest>> GetActiveQuestsAsync(uint characterId) => throw new NotSupportedException();
        public Task CompleteQuestAsync(uint characterId, uint questId) => throw new NotSupportedException();
        public Task GiveUpQuestAsync(uint characterId, uint questId) => throw new NotSupportedException();
        public Task SetQuestsTracedAsync(uint characterId, IReadOnlyCollection<uint> tracedQuestIds) => throw new NotSupportedException();
        public Task SaveProgressAsync(uint characterId, uint questId, string? progressJson) => throw new NotSupportedException();
    }

    private static QuestData Row(uint id) => new() { Quest_ID = id, Start_Type = 4, Start_Type_ID = 10, MAX_Base_Level = 20 };

    private static ProJobRange? Range(uint id) => new ProJobRange(1, 3);

    [Fact]
    public async Task AcceptsDueQuestsInIdOrder_SkippingCompletedAndHeld_CallbackFollowsEachAccept()
    {
        var repo = new Repo(3);
        var held = new HashSet<uint> { 2 };
        var calls = new List<string>();
        await new QuestLevelGrantUseCase(repo).GrantAsync(1, [Row(5), Row(4), Row(3), Row(1)], held.Contains, Range, 12, 2,
            q => { calls.Add($"cb{q.Quest_ID}:{string.Join(",", repo.Accepted)}"); return Task.CompletedTask; });
        Assert.Equal([1u, 4u, 5u], repo.Accepted);
        Assert.Equal(["cb1:1", "cb4:1,4", "cb5:1,4,5"], calls);
    }

    [Fact]
    public async Task HeldDuringTheCallback_IsReReadBeforeTheNextAccept()
    {
        var repo = new Repo();
        var held = new HashSet<uint>();
        await new QuestLevelGrantUseCase(repo).GrantAsync(1, [Row(1), Row(2)], held.Contains, Range, 12, 2,
            q => { held.Add(2); return Task.CompletedTask; });
        Assert.Equal([1u], repo.Accepted);
    }

    [Fact]
    public async Task NothingDue_DoesNotReadCompletions()
    {
        var repo = new Repo();
        await new QuestLevelGrantUseCase(repo).GrantAsync(1, [Row(1)], _ => false, Range, 9, 2, _ => throw new InvalidOperationException());
        Assert.Equal(0, repo.CompletedReads);
        Assert.Empty(repo.Accepted);
    }
}
