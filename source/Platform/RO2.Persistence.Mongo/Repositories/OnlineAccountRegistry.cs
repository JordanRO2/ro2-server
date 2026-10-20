using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Persistence.Mongo.Persistence;

namespace RO2.Persistence.Mongo.Repositories;

/// <summary>
/// Tracks which accounts hold a Zone connection, shared by the Login and Zone processes through the
/// <c>online_accounts</c> collection. Login uses it to refuse a second login to an online account (login answer
/// RMI 12502 with code 1008) and to ask the Zone to drop the first connection (the dropped client receives RMI 2305
/// with disconnect reason 15310, which shows a dialog and ends its session). The Zone writes an entry on connect
/// (RMI 1303), keeps it alive while the account is in the world, hands it over to the connection a channel move
/// reconnects on (RMI 1420; that connection authenticates with RMI 1421 and never sends 1303), and removes it on
/// logout (RMI 1302).
/// </summary>
/// <remarks>
/// <para>
/// Liveness is a heartbeat, not the mere presence of a document: the Zone refreshes <c>SeenAt</c> every
/// <see cref="OnlineAccountRules.HeartbeatInterval"/> while the connection is open and deletes the document when it
/// closes. A document whose heartbeat is older than <see cref="OnlineAccountRules.StaleAfter"/> belongs to a Zone
/// process that died or restarted without cleaning up; Login ignores and deletes it, so a Zone restart never leaves
/// its accounts refused.
/// </para>
/// <para>
/// The refusal can be disabled for testing several clients on one account, through the <c>AllowDuplicateLogin</c>
/// setting in the Login configuration (see <c>LoginHandler.AllowsDuplicateLogin</c>). The Zone keeps the entries up
/// to date either way.
/// </para>
/// <para>
/// Registered as a singleton in AddPersistence (stateless over the singleton MongoDbContext).
/// </para>
/// </remarks>
public sealed class OnlineAccountRegistry : IOnlineAccountRegistry
{
    private const string CollectionName = "online_accounts";

    /// <summary>The stored document. Field names are kept stable so existing rows read unchanged.</summary>
    [BsonIgnoreExtraElements]
    private sealed class OnlineAccountDocument
    {
        [BsonId]
        public long AccountId { get; set; }
        public string Owner { get; set; } = string.Empty;
        public DateTime SeenAt { get; set; }
        public DateTime? KickRequestedAt { get; set; }
    }

    private readonly IMongoCollection<OnlineAccountDocument> _entries;

    /// <summary>Creates the registry over <c>online_accounts</c>.</summary>
    public OnlineAccountRegistry(MongoDbContext context)
    {
        _entries = context.GetCollection<OnlineAccountDocument>(CollectionName);
    }

    private static OnlineAccountEntry? ToEntry(OnlineAccountDocument? d) => d is null ? null : new OnlineAccountEntry
    {
        AccountId = (uint)d.AccountId, Owner = d.Owner, SeenAt = d.SeenAt, KickRequestedAt = d.KickRequestedAt,
    };

    /// <inheritdoc/>
    public Task ClaimAsync(uint accountId, string owner, CancellationToken ct = default)
    {
        var doc = new OnlineAccountDocument { AccountId = accountId, Owner = owner, SeenAt = DateTime.UtcNow };
        return _entries.ReplaceOneAsync(e => e.AccountId == accountId, doc, new ReplaceOptions { IsUpsert = true }, ct);
    }

    /// <inheritdoc/>
    public async Task<OnlineAccountEntry?> HeartbeatAsync(uint accountId, string owner, CancellationToken ct = default)
        => ToEntry(await _entries.FindOneAndUpdateAsync(
            Builders<OnlineAccountDocument>.Filter.Where(e => e.AccountId == accountId && e.Owner == owner),
            Builders<OnlineAccountDocument>.Update.Set(e => e.SeenAt, DateTime.UtcNow),
            new FindOneAndUpdateOptions<OnlineAccountDocument> { ReturnDocument = ReturnDocument.After },
            ct));

    /// <inheritdoc/>
    public async Task<bool> TransferAsync(uint accountId, string fromOwner, string toOwner, CancellationToken ct = default)
    {
        var result = await _entries.UpdateOneAsync(
            e => e.AccountId == accountId && e.Owner == fromOwner,
            Builders<OnlineAccountDocument>.Update.Set(e => e.Owner, toOwner).Set(e => e.SeenAt, DateTime.UtcNow),
            cancellationToken: ct);
        return result.MatchedCount == 1;
    }

    /// <inheritdoc/>
    public Task ReleaseAsync(uint accountId, string owner, CancellationToken ct = default)
        => _entries.DeleteOneAsync(e => e.AccountId == accountId && e.Owner == owner, ct);

    /// <inheritdoc/>
    public Task ReleaseAnyAsync(uint accountId, IReadOnlyCollection<string> owners, CancellationToken ct = default)
        => _entries.DeleteOneAsync(
            Builders<OnlineAccountDocument>.Filter.Eq(e => e.AccountId, (long)accountId)
            & Builders<OnlineAccountDocument>.Filter.In(e => e.Owner, owners),
            ct);

    /// <inheritdoc/>
    public async Task<OnlineAccountEntry?> GetAsync(uint accountId, CancellationToken ct = default)
        => ToEntry(await _entries.Find(e => e.AccountId == accountId).FirstOrDefaultAsync(ct));

    /// <inheritdoc/>
    public Task RequestKickAsync(uint accountId, string owner, CancellationToken ct = default)
        => _entries.UpdateOneAsync(
            e => e.AccountId == accountId && e.Owner == owner && e.KickRequestedAt == null,
            Builders<OnlineAccountDocument>.Update.Set(e => e.KickRequestedAt, DateTime.UtcNow),
            cancellationToken: ct);
}
