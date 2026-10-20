using MongoDB.Driver;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Persistence.Mongo.Persistence;

namespace RO2.Persistence.Mongo.Repositories;

/// <summary>
/// MongoDB implementation of <see cref="ICardRegistryRepository"/> over the <c>card_registries</c> collection
/// (one document per character, <c>_id</c> = the character id). Stateless over the process-wide
/// <see cref="MongoDbContext"/>, so the Zone host registers it as a singleton.
/// </summary>
public sealed class CardRegistryRepository : ICardRegistryRepository
{
    private const string CollectionName = "card_registries";

    private readonly IMongoCollection<CardRegistryDocument> _docs;

    public CardRegistryRepository(MongoDbContext context)
    {
        _docs = context.GetCollection<CardRegistryDocument>(CollectionName);
    }

    public async Task<CardRegistryDocument> GetAsync(uint characterId, CancellationToken ct = default)
    {
        var doc = await _docs.Find(d => d.Id == characterId).FirstOrDefaultAsync(ct);
        return doc ?? new CardRegistryDocument { Id = characterId };
    }

    public Task SaveAsync(CardRegistryDocument doc, CancellationToken ct = default)
    {
        doc.UpdatedAt = DateTime.UtcNow;
        return _docs.ReplaceOneAsync(d => d.Id == doc.Id, doc, new ReplaceOptions { IsUpsert = true }, ct);
    }
}
