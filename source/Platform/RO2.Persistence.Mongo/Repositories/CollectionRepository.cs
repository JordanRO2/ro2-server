using MongoDB.Driver;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Persistence.Mongo.Persistence;

namespace RO2.Persistence.Mongo.Repositories;

/// <summary>
/// MongoDB implementation of <see cref="ICollectionRepository"/>, storing the Collection book in the
/// <c>collections</c> collection (db ro2_server), one document per character keyed by character id. The
/// single index (unique _id) is implicit, so the id lookup is a primary-key hit and no extra index build is
/// needed. It takes the process-wide <see cref="MongoDbContext"/> singleton.
/// </summary>
public sealed class CollectionRepository : ICollectionRepository
{
    private readonly IMongoCollection<CollectionDocument> _collections;

    public CollectionRepository(MongoDbContext context)
    {
        _collections = context.GetCollection<CollectionDocument>("collections");
    }

    public async Task<CollectionDocument> GetOrCreateAsync(uint characterId, CancellationToken ct = default)
    {
        var doc = await _collections.Find(d => d.Id == characterId).FirstOrDefaultAsync(ct);
        return doc ?? new CollectionDocument { Id = characterId };
    }

    public Task SaveAsync(CollectionDocument doc, CancellationToken ct = default) =>
        _collections.ReplaceOneAsync(
            d => d.Id == doc.Id,
            doc,
            new ReplaceOptions { IsUpsert = true },
            ct);
}
