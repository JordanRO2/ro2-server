using MongoDB.Driver;
using RO2.Worlds.Application;
using RO2.Worlds.Domain;
using RO2.Persistence.Mongo.Persistence;

namespace RO2.Persistence.Mongo.Repositories;

/// <summary>
/// MongoDB store for <see cref="IIllusionPyramidStateRepository"/>, one document per character.
/// </summary>
public sealed class IllusionPyramidStateRepository : IIllusionPyramidStateRepository
{
    private const string CollectionName = "character_illusion_pyramid";

    private readonly IMongoCollection<IllusionPyramidStateDocument> _docs;

    public IllusionPyramidStateRepository(MongoDbContext context)
        => _docs = context.GetCollection<IllusionPyramidStateDocument>(CollectionName);

    public async Task<IllusionPyramidStateDocument> GetAsync(uint characterId, CancellationToken ct = default)
        => await _docs.Find(d => d.Id == characterId).FirstOrDefaultAsync(ct)
           ?? new IllusionPyramidStateDocument { Id = characterId };

    public Task SaveAsync(IllusionPyramidStateDocument doc, CancellationToken ct = default)
    {
        doc.UpdatedUtc = DateTime.UtcNow;
        return _docs.ReplaceOneAsync(d => d.Id == doc.Id, doc, new ReplaceOptions { IsUpsert = true }, ct);
    }
}
