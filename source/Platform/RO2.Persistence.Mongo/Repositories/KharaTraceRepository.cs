using MongoDB.Driver;
using RO2.Application.Interfaces;
using RO2.Persistence.Mongo.Persistence;

namespace RO2.Persistence.Mongo.Repositories;

/// <summary>
/// MongoDB store for <see cref="IKharaTraceRepository"/>, holding each character's Khara tracker pins in the
/// <c>khara_traces</c> collection. The list is stored exactly as received, with order and duplicates preserved.
/// </summary>
public sealed class KharaTraceRepository : IKharaTraceRepository
{
 /// <summary>One character's trace list. <c>Id</c> maps to Mongo <c>_id</c> and is the character id.</summary>
    private sealed class KharaTraceDocument
    {
        public uint Id { get; set; }
        public List<uint> KharaIds { get; set; } = new();
    }

    private readonly IMongoCollection<KharaTraceDocument> _traces;

    public KharaTraceRepository(MongoDbContext context)
    {
        _traces = context.GetCollection<KharaTraceDocument>("khara_traces");
    }

    public async Task<IReadOnlyList<uint>> GetAsync(uint characterId, CancellationToken ct = default)
    {
        var doc = await _traces.Find(d => d.Id == characterId).FirstOrDefaultAsync(ct);
        return doc?.KharaIds ?? new List<uint>();
    }

    public Task SaveAsync(uint characterId, IReadOnlyList<uint> kharaIds, CancellationToken ct = default) =>
        _traces.ReplaceOneAsync(
            d => d.Id == characterId,
            new KharaTraceDocument { Id = characterId, KharaIds = kharaIds.ToList() },
            new ReplaceOptions { IsUpsert = true },
            ct);
}
