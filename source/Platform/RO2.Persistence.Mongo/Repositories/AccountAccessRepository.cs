using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using RO2.Application.Interfaces;
using RO2.Persistence.Mongo.Persistence;

namespace RO2.Persistence.Mongo.Repositories;

/// <summary>
/// Reads an account's <c>AccessLevel</c> from the shared <c>accounts</c> collection (the same documents the
/// Identity service owns and writes). Uses a minimal, extra-element-tolerant read model so it never fights
/// the full Identity account schema — it only needs the id and the access byte.
/// </summary>
public sealed class AccountAccessRepository : IAccountAccessRepository
{
    private const string CollectionName = "accounts";

    /// <summary>Access level of a normal player; also the result when the account has no document.</summary>
    private const byte PlayerAccessLevel = 0;

    private readonly IMongoCollection<AccountAccessDoc> _accounts;

    public AccountAccessRepository(MongoDbContext context)
    {
        _accounts = context.GetCollection<AccountAccessDoc>(CollectionName);
    }

    public async Task<byte> GetAccessLevelAsync(uint accountId)
    {
        var doc = await _accounts.Find(a => a.Id == accountId).FirstOrDefaultAsync();
        return doc?.AccessLevel ?? PlayerAccessLevel;
    }

    /// <summary>
    /// Minimal read model over the accounts collection. <see cref="BsonIgnoreExtraElementsAttribute"/> so the
    /// full account document (username, hashes, ban, …) deserializes without a schema fight; the real
    /// <c>_id</c> is a BSON Int32 (verified in the Identity <c>AccountDocument</c> mapping), so map it
    /// explicitly to the <see cref="uint"/> id.
    /// </summary>
    [BsonIgnoreExtraElements]
    public sealed class AccountAccessDoc
    {
        [BsonId]
        [BsonRepresentation(BsonType.Int32)]
        public uint Id { get; set; }

        public byte AccessLevel { get; set; }
    }
}
