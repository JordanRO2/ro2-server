using MongoDB.Driver;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Persistence.Mongo.Persistence;

namespace RO2.Persistence.Mongo.Repositories;

/// <summary>
/// MongoDB store for <see cref="IPlayTimeRewardRepository"/>, one document per account in the
/// <c>account_playtime_reward</c> collection. Holds raw document I/O only; the Zone service owns the cache, the
/// credit and save timing, and the chain rules, and wraps these calls in its own error handling.
/// </summary>
public sealed class PlayTimeRewardRepository : IPlayTimeRewardRepository
{
    private const string CollectionName = "account_playtime_reward";

    private readonly IMongoCollection<PlayTimeRewardDocument> _docs;

    public PlayTimeRewardRepository(MongoDbContext context)
        => _docs = context.GetCollection<PlayTimeRewardDocument>(CollectionName);

    public async Task<PlayTimeRewardDocument?> GetDocumentAsync(uint accountId, CancellationToken ct = default)
        => await _docs.Find(d => d.Id == accountId).FirstOrDefaultAsync(ct);

    public Task SaveDocumentAsync(PlayTimeRewardDocument doc, CancellationToken ct = default)
        => _docs.ReplaceOneAsync(d => d.Id == doc.Id, doc, new ReplaceOptions { IsUpsert = true }, ct);

    public Task SaveManyDocumentsAsync(IReadOnlyCollection<PlayTimeRewardDocument> docs, CancellationToken ct = default)
    {
        if (docs.Count == 0)
            return Task.CompletedTask;
        var models = docs.Select(doc => (WriteModel<PlayTimeRewardDocument>)new ReplaceOneModel<PlayTimeRewardDocument>(
            Builders<PlayTimeRewardDocument>.Filter.Eq(x => x.Id, doc.Id), doc) { IsUpsert = true });
        return _docs.BulkWriteAsync(models, new BulkWriteOptions { IsOrdered = false }, ct);
    }
}
