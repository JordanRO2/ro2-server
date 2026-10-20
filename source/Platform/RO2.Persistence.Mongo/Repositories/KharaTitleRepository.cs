using MongoDB.Driver;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Persistence.Mongo.Persistence;

namespace RO2.Persistence.Mongo.Repositories;

/// <summary>
/// MongoDB store for <see cref="IKharaTitleRepository"/>, one document per character. Holds raw document I/O only;
/// the Zone service owns the title rules, the published-titles cache and the play-clock time base.
/// </summary>
public sealed class KharaTitleRepository : IKharaTitleRepository
{
    private const string CollectionName = "character_khara_title";

    private readonly IMongoCollection<KharaTitleDocument> _docs;

    public KharaTitleRepository(MongoDbContext context)
        => _docs = context.GetCollection<KharaTitleDocument>(CollectionName);

    public async Task<KharaTitleDocument?> GetAsync(uint characterId, CancellationToken ct = default)
        => await _docs.Find(d => d.Id == characterId).FirstOrDefaultAsync(ct);

    public Task SaveAsync(KharaTitleDocument doc, CancellationToken ct = default)
        => _docs.ReplaceOneAsync(d => d.Id == doc.Id, doc, new ReplaceOptions { IsUpsert = true }, ct);

    public Task StorePlayClockAsync(uint characterId, DateTime playClockUtc, DateTime updatedUtc, CancellationToken ct = default)
    {
        var update = Builders<KharaTitleDocument>.Update
            .Set(d => d.PlayClockUtc, playClockUtc)
            .Set(d => d.UpdatedUtc, updatedUtc);
        return _docs.UpdateOneAsync(d => d.Id == characterId, update, new UpdateOptions { IsUpsert = false }, ct);
    }
}
