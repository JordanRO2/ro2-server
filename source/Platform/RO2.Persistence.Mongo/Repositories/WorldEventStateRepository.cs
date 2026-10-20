using MongoDB.Driver;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Persistence.Mongo.Persistence;

namespace RO2.Persistence.Mongo.Repositories;

/// <summary>
/// MongoDB store for <see cref="IWorldEventStateRepository"/>, holding the live state of each world event in the
/// <c>world_event_state</c> collection, one document per event keyed by World_Event_ID. State is server-wide and
/// persisted because a stage can last several days.
/// </summary>
public sealed class WorldEventStateRepository : IWorldEventStateRepository
{
    private readonly IMongoCollection<WorldEventStateDocument> _states;

    public WorldEventStateRepository(MongoDbContext context)
    {
        _states = context.GetCollection<WorldEventStateDocument>("world_event_state");
    }

    public async Task<IReadOnlyList<WorldEventStateDocument>> GetAllAsync(CancellationToken ct = default)
        => await _states.Find(_ => true).ToListAsync(ct);

    public Task SaveAsync(WorldEventStateDocument doc, CancellationToken ct = default) =>
        _states.ReplaceOneAsync(d => d.Id == doc.Id, doc, new ReplaceOptions { IsUpsert = true }, ct);
}
