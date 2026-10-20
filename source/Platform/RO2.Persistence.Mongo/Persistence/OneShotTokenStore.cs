using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using RO2.Application.Interfaces;

namespace RO2.Persistence.Mongo.Persistence;

/// <summary>
/// MongoDB implementation of <see cref="IOneShotTokenStore"/> (SP0-TOKEN). Issue inserts a document keyed by
/// the token (so uniqueness is Mongo-enforced); consume is a <c>findOneAndDelete</c> whose filter requires the
/// token, purpose, unexpired and (when given) account, so a second consume from any process returns null and a
/// wrong purpose/account never burns the token. The constructor does no I/O (the boot-stall lesson of
/// MailRepository/AuctionHouse): the TTL index is created by <see cref="EnsureIndexesAsync"/>, which the host
/// calls off the startup path, and is only the garbage collector — the consume filter enforces expiry.
/// </summary>
public sealed class OneShotTokenStore : IOneShotTokenStore
{
    public const string CollectionName = "one_shot_tokens";

 /// <summary>Name of the TTL index on <c>Expiry</c>.</summary>
    public const string ExpiryIndexName = "ix_one_shot_tokens_expiry_ttl";

 /// <summary>Stored shape. <c>_id</c> is the token, so uniqueness is enforced by Mongo. Account and
 /// character ids are stored as Int64 (a ulong above long.MaxValue round-trips through an unchecked cast).</summary>
    [BsonIgnoreExtraElements]
    internal sealed class TokenDocument
    {
        [BsonId] public string Id { get; set; } = "";
        public string Purpose { get; set; } = "";
        public long AccountId { get; set; }
        public long CharacterUid { get; set; }
        public DateTime IssuedUtc { get; set; }

 /// <summary>The TTL field.</summary>
        public DateTime Expiry { get; set; }
    }

    private readonly IMongoCollection<TokenDocument> _tokens;
    private readonly TimeProvider _time;

    public OneShotTokenStore(MongoDbContext db, TimeProvider? time = null)
    {
        _tokens = db.GetCollection<TokenDocument>(CollectionName);
        _time = time ?? TimeProvider.System;
    }

 /// <summary>The TTL index: Mongo removes a document once <c>Expiry</c> has passed (<c>expireAfter</c> 0 s).</summary>
    internal static CreateIndexModel<TokenDocument> ExpiryIndexModel() => new(
        Builders<TokenDocument>.IndexKeys.Ascending(d => d.Expiry),
        new CreateIndexOptions { Name = ExpiryIndexName, ExpireAfter = TimeSpan.Zero });

 /// <summary>
 /// The exact filter <see cref="ConsumeAsync"/> hands to <c>findOneAndDelete</c>: the token, the purpose,
 /// still unexpired, and (when given) the account it was issued to.
 /// </summary>
    internal static FilterDefinition<TokenDocument> ConsumeFilter(
        string token, string purpose, DateTime nowUtc, uint? accountId = null)
    {
        var b = Builders<TokenDocument>.Filter;
        var filter = b.And(b.Eq(d => d.Id, token), b.Eq(d => d.Purpose, purpose), b.Gt(d => d.Expiry, nowUtc));
        return accountId is { } account ? b.And(filter, b.Eq(d => d.AccountId, (long)account)) : filter;
    }

    internal static TokenDocument ToDocument(
        string token, string purpose, uint accountId, ulong characterUid, DateTime nowUtc, TimeSpan lifetime)
        => new()
        {
            Id = token,
            Purpose = purpose,
            AccountId = accountId,
            CharacterUid = unchecked((long)characterUid),
            IssuedUtc = nowUtc,
            Expiry = nowUtc + lifetime,
        };

    internal static OneShotToken ToToken(TokenDocument d) => new(
        d.Id, d.Purpose, checked((uint)d.AccountId), unchecked((ulong)d.CharacterUid), d.IssuedUtc, d.Expiry);

    public async Task<bool> EnsureIndexesAsync(CancellationToken ct = default)
    {
        try
        {
            await _tokens.Indexes.CreateOneAsync(ExpiryIndexModel(), cancellationToken: ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            return false;
        }
    }

    public async Task<bool> IssueAsync(
        string token, string purpose, uint accountId, ulong characterUid, TimeSpan lifetime,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        ArgumentException.ThrowIfNullOrEmpty(purpose);
        if (lifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(lifetime), "token lifetime must be positive");

        var doc = ToDocument(token, purpose, accountId, characterUid, _time.GetUtcNow().UtcDateTime, lifetime);
        try
        {
            await _tokens.InsertOneAsync(doc, cancellationToken: ct).ConfigureAwait(false);
            return true;
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
    }

    public async Task<string> IssueNewAsync(
        string purpose, uint accountId, ulong characterUid, TimeSpan lifetime, CancellationToken ct = default)
    {
        var token = OneShotTokens.NewToken();
        if (!await IssueAsync(token, purpose, accountId, characterUid, lifetime, ct).ConfigureAwait(false))
            throw new InvalidOperationException("one-shot token collision"); // 128 random bits: not expected
        return token;
    }

    public async Task<OneShotToken?> ConsumeAsync(
        string token, string purpose, uint? accountId = null, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(purpose))
            return null;

        var filter = ConsumeFilter(token, purpose, _time.GetUtcNow().UtcDateTime, accountId);
        var doc = await _tokens.FindOneAndDeleteAsync(filter, cancellationToken: ct).ConfigureAwait(false);
        return doc is null ? null : ToToken(doc);
    }
}
