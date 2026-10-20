using MongoDB.Driver;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Persistence.Mongo.Persistence;

namespace RO2.Persistence.Mongo.Repositories;

/// <summary>
/// MongoDB store for <see cref="IMonsterCollectionProgressRepository"/>, one document per character in the
/// <c>collection_monster_progress</c> collection. Stateless over the process-wide <see cref="MongoDbContext"/>,
/// so it is registered as a singleton.
/// </summary>
public sealed class MonsterCollectionProgressRepository : IMonsterCollectionProgressRepository
{
    private const string CollectionName = "collection_monster_progress";

    private readonly IMongoCollection<MonsterCollectionProgressDocument> _docs;

    public MonsterCollectionProgressRepository(MongoDbContext context)
        => _docs = context.GetCollection<MonsterCollectionProgressDocument>(CollectionName);

    public async Task<MonsterCollectionProgressDocument?> GetAsync(uint characterId, CancellationToken ct = default)
        => await _docs.Find(d => d.Id == characterId).FirstOrDefaultAsync(ct);

    public async Task SaveAsync(MonsterCollectionProgressDocument doc, CancellationToken ct = default)
        => await _docs.ReplaceOneAsync(d => d.Id == doc.Id, doc, new ReplaceOptions { IsUpsert = true }, ct);
}
