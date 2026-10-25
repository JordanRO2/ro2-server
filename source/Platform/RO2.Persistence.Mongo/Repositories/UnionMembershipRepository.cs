using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Persistence.Mongo.Persistence;
using RO2.Characters.Domain;

namespace RO2.Persistence.Mongo.Repositories;

/// <summary>
/// MongoDB store for <see cref="IUnionMembershipRepository"/>, one membership per character in the
/// <c>union_memberships</c> collection. The insert on <c>_id</c> keeps a single membership per character under
/// concurrent join requests (RMI 1582).
/// </summary>
public sealed class UnionMembershipRepository : IUnionMembershipRepository
{
    private const string CollectionName = "union_memberships";

    [BsonIgnoreExtraElements]
    private sealed class UnionMembershipDocument
    {
        [BsonId] public uint CharacterId { get; set; }
        public uint UnionId { get; set; }
        public long JoinedAtUnix { get; set; }
    }

    private readonly IMongoCollection<UnionMembershipDocument> _docs;

    public UnionMembershipRepository(MongoDbContext context)
    {
        _docs = context.GetCollection<UnionMembershipDocument>(CollectionName);
    }

    public async Task<UnionMembership?> GetAsync(uint characterId, CancellationToken ct = default)
    {
        var doc = await _docs.Find(d => d.CharacterId == characterId).FirstOrDefaultAsync(ct);
        return doc is null ? null : new UnionMembership(doc.UnionId, doc.JoinedAtUnix);
    }

    public async Task<bool> JoinAsync(uint characterId, uint unionId, long joinedAtUnix, CancellationToken ct = default)
    {
        try
        {
            await _docs.InsertOneAsync(
                new UnionMembershipDocument { CharacterId = characterId, UnionId = unionId, JoinedAtUnix = joinedAtUnix },
                cancellationToken: ct);
            return true;
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
    }

    public async Task<bool> WithdrawAsync(uint characterId, uint unionId, CancellationToken ct = default)
    {
        var result = await _docs.DeleteOneAsync(d => d.CharacterId == characterId && d.UnionId == unionId, ct);
        return result.DeletedCount > 0;
    }
}
