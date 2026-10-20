using MongoDB.Driver;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Persistence.Mongo.Persistence;

namespace RO2.Persistence.Mongo.Repositories;

/// <summary>
/// MongoDB store for <see cref="IKharaStateRepository"/>, one document per character. Holds raw document I/O only;
/// the Zone service owns the progress rules, the published-state cache and the per-character gate.
/// </summary>
public sealed class KharaStateRepository : IKharaStateRepository
{
    private const string CollectionName = "character_khara";

    private readonly IMongoCollection<KharaStateDocument> _docs;

    public KharaStateRepository(MongoDbContext context)
        => _docs = context.GetCollection<KharaStateDocument>(CollectionName);

    public async Task<KharaStateDocument?> GetAsync(uint characterId, CancellationToken ct = default)
        => await _docs.Find(d => d.Id == characterId).FirstOrDefaultAsync(ct);

    public Task SaveAsync(KharaStateDocument doc, CancellationToken ct = default)
        => _docs.ReplaceOneAsync(d => d.Id == doc.Id, doc, new ReplaceOptions { IsUpsert = true }, ct);
}
