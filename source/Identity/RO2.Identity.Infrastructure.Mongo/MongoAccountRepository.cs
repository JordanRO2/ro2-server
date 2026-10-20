using System.Runtime.CompilerServices;
using MongoDB.Bson;
using MongoDB.Driver;
using RO2.BuildingBlocks.Domain;
using RO2.Identity.Domain.Account;

namespace RO2.Identity.Infrastructure.Mongo;

/// <summary>
/// Mongo-backed <see cref="IAccountRepository"/> over the Identity-owned accounts collection. The live store is
/// <c>ro2_server.accounts</c> (the existing accounts) by default; a dedicated <c>ro2_identity</c> database is a
/// later config-only db-per-service split (see <see cref="IdentityMongoContext"/>). Pure persistence — no auth
/// logic (that all moved into the aggregate). Maps the <see cref="Account"/> aggregate to/from <see cref="AccountDocument"/>.
/// </summary>
/// <remarks>
/// Hardened against registration races and lost updates (S-H9): usernames are unique case-insensitively through a
/// collated unique index and looked up with the same collation; ids come from an atomic counter document
/// (<c>findOneAndUpdate $inc</c>) that is only ever raised to the collection's current max id; an insert that hits a
/// duplicate key surfaces as a <see cref="DomainException"/> instead of an uncaught driver exception; and
/// <see cref="UpdateAsync"/> <c>$set</c>s only the fields this aggregate changed since it was loaded, so a concurrent
/// PIN/last-login write (or the legacy embedded <c>Characters</c> array) is never clobbered by a whole-document replace.
/// </remarks>
public sealed class MongoAccountRepository : IAccountRepository
{
    /// <summary>Name of the case-insensitive unique index on <c>Username</c>.</summary>
    public const string UsernameIndexName = "ux_accounts_username_ci";

    /// <summary>The collection that holds the per-accounts-collection id counters.</summary>
    public const string CountersCollectionName = "identity_counters";

    // Strength 2 = case-insensitive, accent-sensitive. The index and every username lookup share it.
    private static readonly Collation UsernameCollation = new(UsernameCollationLocale, strength: CollationStrength.Secondary);
    private static readonly FindOptions UsernameFind = new() { Collation = UsernameCollation };

    // Document field names used in raw BSON filters and updates (the typed builders use the AccountDocument members).
    private const string UsernameField = "Username";
    private const string IdField = "_id";

    // Counter document: _id is the counted collection's name, "seq" is the last id handed out.
    private const string CounterSequenceField = "seq";

    // BSON type alias of the partial-index filter: only documents whose Username is a string are indexed.
    private const string StringBsonType = "string";

    // Collation locale shared by the unique index and every username lookup.
    private const string UsernameCollationLocale = "en";

    // Pre-existing case-variant pairs ("Bob"/"bob") may predate the unique index; a login prefers the exact-case row.
    private const int LookupLimit = 8;

    // The mutable state an aggregate had when this repository last loaded or saved it, so UpdateAsync can write
    // only what changed. An aggregate that never came through here has no entry and gets every mutable field.
    private static readonly ConditionalWeakTable<Account, AccountState> Baselines = new();

    private readonly IMongoCollection<AccountDocument> _accounts;
    private readonly IMongoCollection<BsonDocument> _counters;
    private readonly SemaphoreSlim _indexGate = new(1, 1);
    private volatile bool _indexesEnsured;

    /// <summary>Creates the repository over the given context's accounts collection.</summary>
    /// <param name="context">The Identity Mongo context (owns the account-store connection, default <c>ro2_server</c>).</param>
    public MongoAccountRepository(IdentityMongoContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _accounts = context.Accounts;
        _counters = _accounts.Database.GetCollection<BsonDocument>(CountersCollectionName);
    }

    /// <summary>
    /// Whether the unique username index exists. <see langword="false"/> before <see cref="EnsureIndexesAsync"/>
    /// has run, or when the build was refused (existing duplicate usernames must be merged by the operator first).
    /// </summary>
    public bool UsernameUniquenessEnforced { get; private set; }

    /// <summary>
    /// Creates the unique username index (idempotent; the first <see cref="AddAsync"/> calls it lazily). A refused
    /// build, e.g. because the collection already holds case-variant duplicates, leaves the repository working
    /// without the backstop and reports <see langword="false"/>.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns><see langword="true"/> when username uniqueness is enforced by the store.</returns>
    public async Task<bool> EnsureIndexesAsync(CancellationToken cancellationToken = default)
    {
        if (_indexesEnsured)
        {
            return UsernameUniquenessEnforced;
        }

        await _indexGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_indexesEnsured)
            {
                return UsernameUniquenessEnforced;
            }

            try
            {
                await _accounts.Indexes.CreateOneAsync(
                    new CreateIndexModel<AccountDocument>(
                        Builders<AccountDocument>.IndexKeys.Ascending(a => a.Username),
                        new CreateIndexOptions<AccountDocument>
                        {
                            Name = UsernameIndexName,
                            Unique = true,
                            Collation = UsernameCollation,
                            PartialFilterExpression = new BsonDocumentFilterDefinition<AccountDocument>(
                                new BsonDocument(UsernameField, new BsonDocument("$type", StringBsonType))),
                        }),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                UsernameUniquenessEnforced = true;
            }
            catch (MongoCommandException)
            {
                UsernameUniquenessEnforced = false;
            }

            _indexesEnsured = true;
            return UsernameUniquenessEnforced;
        }
        finally
        {
            _indexGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<Account?> GetByIdAsync(AccountId id, CancellationToken cancellationToken = default)
    {
        var doc = await _accounts.Find(a => a.Id == id.Value)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return doc is null ? null : ToAggregate(doc);
    }

    /// <inheritdoc />
    public async Task<Account?> GetByUsernameAsync(string username, CancellationToken cancellationToken = default)
    {
        var docs = await _accounts.Find(a => a.Username == username, UsernameFind)
            .Limit(LookupLimit)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var doc = docs.Find(d => string.Equals(d.Username, username, StringComparison.Ordinal))
            ?? docs.FirstOrDefault();
        return doc is null ? null : ToAggregate(doc);
    }

    /// <inheritdoc />
    public async Task<bool> UsernameExistsAsync(string username, CancellationToken cancellationToken = default)
    {
        return await _accounts.Find(a => a.Username == username, UsernameFind)
            .AnyAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<AccountId> NextIdAsync(CancellationToken cancellationToken = default)
    {
        // Atomic counter instead of the racy max(Id)+1. The counter is first raised to the collection's current
        // max id ($max never lowers it), so ids stay dense above any account inserted outside the counter.
        var maxDoc = await _accounts.Find(FilterDefinition<AccountDocument>.Empty)
            .SortByDescending(a => a.Id)
            .Limit(1)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var floor = (long)(maxDoc?.Id ?? 0u);

        var filter = Builders<BsonDocument>.Filter.Eq(IdField, _accounts.CollectionNamespace.CollectionName);
        var raise = Builders<BsonDocument>.Update.Max(CounterSequenceField, floor);
        try
        {
            await _counters.UpdateOneAsync(filter, raise, new UpdateOptions { IsUpsert = true }, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            // Two first calls raced the upsert; the document now exists, so a plain update applies the floor.
            await _counters.UpdateOneAsync(filter, raise, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        var counter = await _counters.FindOneAndUpdateAsync(
            filter,
            Builders<BsonDocument>.Update.Inc(CounterSequenceField, 1L),
            new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After },
            cancellationToken).ConfigureAwait(false);

        return new AccountId(checked((uint)counter[CounterSequenceField].ToInt64()));
    }

    /// <inheritdoc />
    /// <exception cref="DomainException">Thrown when the username (case-insensitively) or the id is already taken.</exception>
    public async Task AddAsync(Account aggregate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        await EnsureIndexesAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _accounts.InsertOneAsync(ToDocument(aggregate), options: null, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            throw new DomainException($"account {aggregate.Id.Value} was rejected: username or id already exists");
        }

        Baselines.AddOrUpdate(aggregate, AccountState.Of(aggregate));
    }

    /// <inheritdoc />
    public async Task UpdateAsync(Account aggregate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        var current = AccountState.Of(aggregate);
        Baselines.TryGetValue(aggregate, out var baseline);

        var update = Builders<AccountDocument>.Update;
        var sets = new List<UpdateDefinition<AccountDocument>>();
        if (baseline is null || baseline.PasswordHash != current.PasswordHash)
        {
            sets.Add(update.Set(a => a.PasswordHash, current.PasswordHash));
        }

        if (baseline is null || baseline.SecondPasswordHash != current.SecondPasswordHash)
        {
            sets.Add(update.Set(a => a.SecondPasswordHash, current.SecondPasswordHash));
        }

        if (baseline is null || baseline.Email != current.Email)
        {
            sets.Add(update.Set(a => a.Email, current.Email));
        }

        if (baseline is null || baseline.LastLogin != current.LastLogin)
        {
            sets.Add(update.Set(a => a.LastLogin, current.LastLogin));
        }

        if (baseline is null || baseline.IsBanned != current.IsBanned)
        {
            sets.Add(update.Set(a => a.IsBanned, current.IsBanned));
        }

        if (baseline is null || baseline.BanExpires != current.BanExpires)
        {
            sets.Add(update.Set(a => a.BanExpires, current.BanExpires));
        }

        if (baseline is null || baseline.BanReason != current.BanReason)
        {
            sets.Add(update.Set(a => a.BanReason, current.BanReason));
        }

        if (baseline is null || baseline.AccessLevel != current.AccessLevel)
        {
            sets.Add(update.Set(a => a.AccessLevel, current.AccessLevel));
        }

        if (sets.Count > 0)
        {
            await _accounts.UpdateOneAsync(a => a.Id == aggregate.Id.Value, update.Combine(sets),
                options: null, cancellationToken).ConfigureAwait(false);
        }

        Baselines.AddOrUpdate(aggregate, current);
    }

    /// <summary>The mutable fields of an account (username and creation time never change after registration).</summary>
    private sealed record AccountState(
        string PasswordHash,
        string? SecondPasswordHash,
        string? Email,
        DateTime? LastLogin,
        bool IsBanned,
        DateTime? BanExpires,
        string? BanReason,
        byte AccessLevel)
    {
        public static AccountState Of(Account a) => new(
            a.PasswordHash, a.SecondPasswordHash, a.Email.Value, a.LastLogin,
            a.Ban.IsBanned, a.Ban.BanExpires, a.Ban.BanReason, a.AccessLevel);
    }

    private static AccountDocument ToDocument(Account a) => new()
    {
        Id = a.Id.Value,
        Username = a.Username,
        PasswordHash = a.PasswordHash,
        SecondPasswordHash = a.SecondPasswordHash,
        Email = a.Email.Value,
        CreatedAt = a.CreatedAt,
        LastLogin = a.LastLogin,
        IsBanned = a.Ban.IsBanned,
        BanExpires = a.Ban.BanExpires,
        BanReason = a.Ban.BanReason,
        AccessLevel = a.AccessLevel,
    };

    private static Account ToAggregate(AccountDocument d)
    {
        var account = Account.Rehydrate(
            new AccountId(d.Id),
            d.Username,
            d.PasswordHash,
            d.SecondPasswordHash,
            Email.FromStorage(d.Email),
            new BanPolicy(d.IsBanned, d.BanExpires, d.BanReason),
            d.AccessLevel,
            d.CreatedAt,
            d.LastLogin);
        Baselines.AddOrUpdate(account, AccountState.Of(account));
        return account;
    }
}
