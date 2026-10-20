using MongoDB.Driver;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Persistence.Mongo.Persistence;

namespace RO2.Persistence.Mongo.Repositories;

/// <remarks>
/// Store-side hardening (S-H8): a TTL index on <c>ExpiresAt</c> lets Mongo prune expired tokens (the consume
/// path still checks the expiry itself, since the TTL monitor only runs about once a minute), and issuing a
/// token deletes the account's older ones, so an account has one live handoff token at a time.
/// </remarks>
public class ZoneAuthTokenRepository : IZoneAuthTokenRepository
{
    /// <summary>The TTL index: Mongo removes a token once <c>ExpiresAt</c> has passed (<c>expireAfter</c> 0 s).</summary>
    public const string ExpiryIndexName = "ix_zone_auth_tokens_expiry_ttl";

    /// <summary>Supports the per-account supersede delete on issue.</summary>
    public const string AccountIndexName = "ix_zone_auth_tokens_account";

    private static int _indexesEnsured; // 0 = not yet

    private readonly IMongoCollection<ZoneAuthToken> _tokens;

    public ZoneAuthTokenRepository(MongoDbContext context)
    {
        _tokens = context.GetCollection<ZoneAuthToken>("zone_auth_tokens");
    }

    /// <summary>
    /// Creates the expiry TTL index and the account index (idempotent). Called lazily by the first
    /// <see cref="IssueAsync"/>; a failure is reported as <see langword="false"/> and never stops issuance.
    /// </summary>
    public async Task<bool> EnsureIndexesAsync(CancellationToken ct = default)
    {
        try
        {
            await _tokens.Indexes.CreateManyAsync(new[]
            {
                new CreateIndexModel<ZoneAuthToken>(
                    Builders<ZoneAuthToken>.IndexKeys.Ascending(t => t.ExpiresAt),
                    new CreateIndexOptions { Name = ExpiryIndexName, ExpireAfter = TimeSpan.Zero }),
                new CreateIndexModel<ZoneAuthToken>(
                    Builders<ZoneAuthToken>.IndexKeys.Ascending(t => t.AccountId),
                    new CreateIndexOptions { Name = AccountIndexName }),
            }, ct).ConfigureAwait(false);
            return true;
        }
        catch (MongoException)
        {
            return false;
        }
    }

    public async Task IssueAsync(Guid token, uint accountId, TimeSpan ttl)
    {
        if (Volatile.Read(ref _indexesEnsured) == 0
            && await EnsureIndexesAsync().ConfigureAwait(false))
        {
            Volatile.Write(ref _indexesEnsured, 1);
        }

        var doc = new ZoneAuthToken
        {
            Id = token.ToString("D"),
            AccountId = accountId,
            ExpiresAt = DateTime.UtcNow.Add(ttl),
        };
        // Upsert so a regenerated GUID for the same handoff simply overwrites.
        await _tokens.ReplaceOneAsync(t => t.Id == doc.Id, doc, new ReplaceOptions { IsUpsert = true });

        // One live token per account: the new one supersedes every older one (by expiry). Deleting only the
        // older ones keeps two racing issues from deleting each other's token, so the later one always survives.
        await _tokens.DeleteManyAsync(t => t.AccountId == accountId && t.Id != doc.Id && t.ExpiresAt < doc.ExpiresAt);
    }

    public async Task<uint?> ConsumeAsync(Guid token)
    {
        var id = token.ToString("D");
        // One-time use: atomically fetch and delete the token.
        var doc = await _tokens.FindOneAndDeleteAsync(t => t.Id == id);
        if (doc is null) return null;
        if (doc.ExpiresAt < DateTime.UtcNow) return null;   // presented too late
        return doc.AccountId;
    }
}
