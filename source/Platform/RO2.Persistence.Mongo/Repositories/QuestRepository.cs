using MongoDB.Driver;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Domain.Enums;
using RO2.Persistence.Mongo.Persistence;

namespace RO2.Persistence.Mongo.Repositories;

public class QuestRepository : IQuestRepository
{
    private readonly IMongoCollection<CharacterQuest> _quests;

    public QuestRepository(MongoDbContext context)
    {
        _quests = context.GetCollection<CharacterQuest>("character_quests");
    }

    public async Task<IReadOnlyList<CharacterQuest>> GetActiveQuestsAsync(uint characterId)
    {
        return await _quests
            .Find(q => q.CharacterId == characterId && q.Status == QuestStatus.InProgress)
            .SortBy(q => q.StartedAt)
            .ToListAsync();
    }

    public async Task<IReadOnlyList<uint>> GetCompletedQuestIdsAsync(uint characterId)
    {
        var completed = await _quests
            .Find(q => q.CharacterId == characterId && q.Status == QuestStatus.Completed)
            .ToListAsync();
        return completed.Select(q => q.QuestId).ToList();
    }

    public async Task<DateTime?> GetLastCompletedAtAsync(uint characterId, uint questId)
    {
        var last = await _quests
            .Find(q => q.CharacterId == characterId && q.QuestId == questId && q.Status == QuestStatus.Completed)
            .SortByDescending(q => q.CompletedAt)
            .Limit(1)
            .FirstOrDefaultAsync();
        return last?.CompletedAt;
    }

    public async Task AcceptQuestAsync(uint characterId, uint questId)
    {
        var maxId = await _quests.Find(_ => true)
            .SortByDescending(q => q.Id)
            .Limit(1)
            .FirstOrDefaultAsync();

        var nextId = (maxId?.Id ?? 0) + 1;

        var quest = new CharacterQuest
        {
            Id = nextId,
            CharacterId = characterId,
            QuestId = questId,
            Status = QuestStatus.InProgress,
            StartedAt = DateTime.UtcNow
        };

        await _quests.InsertOneAsync(quest);
    }

    public async Task CompleteQuestAsync(uint characterId, uint questId)
    {
        var update = Builders<CharacterQuest>.Update
            .Set(q => q.Status, QuestStatus.Completed)
            .Set(q => q.CompletedAt, DateTime.UtcNow);

        await _quests.UpdateOneAsync(
            q => q.CharacterId == characterId && q.QuestId == questId && q.Status == QuestStatus.InProgress,
            update);
    }

    public async Task GiveUpQuestAsync(uint characterId, uint questId)
    {
        await _quests.DeleteOneAsync(
            q => q.CharacterId == characterId && q.QuestId == questId && q.Status == QuestStatus.InProgress);
    }

    public async Task SetQuestsTracedAsync(uint characterId, IReadOnlyCollection<uint> tracedQuestIds)
    {
        var traced = tracedQuestIds as ISet<uint> ?? new HashSet<uint>(tracedQuestIds);
        
        await _quests.UpdateManyAsync(
            q => q.CharacterId == characterId && q.Status == QuestStatus.InProgress && traced.Contains(q.QuestId),
            Builders<CharacterQuest>.Update.Set(q => q.IsTraced, true));
        await _quests.UpdateManyAsync(
            q => q.CharacterId == characterId && q.Status == QuestStatus.InProgress && !traced.Contains(q.QuestId),
            Builders<CharacterQuest>.Update.Set(q => q.IsTraced, false));
    }

    public async Task SaveProgressAsync(uint characterId, uint questId, string? progressJson)
    {
        await _quests.UpdateOneAsync(
            q => q.CharacterId == characterId && q.QuestId == questId && q.Status == QuestStatus.InProgress,
            Builders<CharacterQuest>.Update.Set(q => q.ProgressJson, progressJson));
    }
}
