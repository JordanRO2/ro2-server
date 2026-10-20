using MongoDB.Bson;
using MongoDB.Driver;
using RO2.Identity.Domain.Account;
using RO2.Identity.Infrastructure.Mongo;
using Xunit;

namespace RO2.Tests.Identity;

/// <summary>
/// S-H9 against a real Mongo: the collated unique username index, the atomic id counter, the registration race
/// and targeted <c>$set</c> updates. Runs in a scratch database that is dropped afterwards, and soft-skips (green
/// no-op) when Mongo is unreachable. Point it at another server with <c>RO2_TEST_MONGO</c>
/// (default <c>mongodb://localhost:27017</c>).
/// </summary>
public sealed class MongoAccountHardeningTests
{
    private sealed class Scratch : IDisposable
    {
        private readonly MongoClient _client;
        public string Database { get; } = "ro2_it_" + Guid.NewGuid().ToString("N");
        public string Collection { get; } = "accounts";
        public IMongoCollection<BsonDocument> Raw { get; }
        public IdentityMongoContext Context { get; }

        public Scratch(MongoClient client, string connectionString)
        {
            _client = client;
            Raw = client.GetDatabase(Database).GetCollection<BsonDocument>(Collection);
            Context = new IdentityMongoContext(connectionString, Database, Collection);
        }

        public MongoAccountRepository NewRepo() => new(Context);

        public void Dispose() => _client.DropDatabase(Database);
    }

    private static Scratch? TryScratch()
    {
        var connectionString = Environment.GetEnvironmentVariable("RO2_TEST_MONGO") ?? "mongodb://localhost:27017";
        try
        {
            var settings = MongoClientSettings.FromConnectionString(connectionString);
            settings.ServerSelectionTimeout = TimeSpan.FromSeconds(2);
            var client = new MongoClient(settings);
            _ = client.ListDatabaseNames(new ListDatabaseNamesOptions());
            return new Scratch(client, connectionString);
        }
        catch (Exception ex) when (ex is TimeoutException or MongoException)
        {
            return null; // Mongo down -> green no-op (documented skip).
        }
    }

    [Fact]
    public async Task Ensure_indexes_creates_the_collated_partial_unique_username_index()
    {
        using var scratch = TryScratch();
        if (scratch is null) return;
        var repo = scratch.NewRepo();

        Assert.True(await repo.EnsureIndexesAsync());
        Assert.True(repo.UsernameUniquenessEnforced);

        var indexes = await (await scratch.Raw.Indexes.ListAsync()).ToListAsync();
        var index = indexes.Single(i => i["name"].AsString == MongoAccountRepository.UsernameIndexName);
        Assert.True(index["unique"].AsBoolean);
        Assert.Equal(2, index["collation"]["strength"].ToInt32());
        Assert.True(index.Contains("partialFilterExpression"));
    }

    [Fact]
    public async Task Ensure_indexes_reports_false_instead_of_throwing_when_existing_usernames_collide()
    {
        using var scratch = TryScratch();
        if (scratch is null) return;
        // Pre-existing accounts always carry a password hash; seed one so the rows rehydrate (Credentials
        // rejects an empty hash). The scenario under test is the colliding username, not the credential.
        await scratch.Raw.InsertManyAsync(new[]
        {
            new BsonDocument { { "_id", 1 }, { "Username", "Dup" }, { "PasswordHash", "$2a$11$placeholderplaceholderplaceholderplaceholderx" } },
            new BsonDocument { { "_id", 2 }, { "Username", "dup" }, { "PasswordHash", "$2a$11$placeholderplaceholderplaceholderplaceholderx" } },
        });

        var repo = scratch.NewRepo();

        Assert.False(await repo.EnsureIndexesAsync());
        Assert.False(repo.UsernameUniquenessEnforced);
        // Logins keep working for both pre-existing case variants: the exact-case row is preferred.
        Assert.Equal(1u, (await repo.GetByUsernameAsync("Dup"))!.Id.Value);
        Assert.Equal(2u, (await repo.GetByUsernameAsync("dup"))!.Id.Value);
    }

    [Fact]
    public async Task A_case_variant_username_is_the_same_account()
    {
        using var scratch = TryScratch();
        if (scratch is null) return;
        await AccountStoreHardeningScenarios.CaseVariantUsernameIsTheSameAccount(scratch.NewRepo(), "mixedname");
    }

    [Fact]
    public async Task Concurrent_SetSecondPassword_and_Authenticate_both_persist()
    {
        using var scratch = TryScratch();
        if (scratch is null) return;
        await AccountStoreHardeningScenarios.SetSecondPasswordAndAuthenticateBothPersist(scratch.NewRepo(), "pin");
    }

    [Fact]
    public async Task NextId_is_atomic_and_stays_above_accounts_inserted_outside_the_counter()
    {
        using var scratch = TryScratch();
        if (scratch is null) return;
        await scratch.Raw.InsertOneAsync(new BsonDocument { { "_id", 50 }, { "Username", "legacy" } });
        var repos = Enumerable.Range(0, 3).Select(_ => scratch.NewRepo()).ToList();

        var ids = await Task.WhenAll(Enumerable.Range(0, 30)
            .Select(i => Task.Run(async () => (await repos[i % repos.Count].NextIdAsync()).Value)));

        Assert.Equal(30, ids.Distinct().Count());
        Assert.All(ids, id => Assert.True(id > 50u));
        Assert.Equal(51u, ids.Min());
        Assert.Equal(80u, ids.Max());
    }

    [Fact]
    public async Task Update_sets_only_changed_fields_and_keeps_the_legacy_Characters_array()
    {
        using var scratch = TryScratch();
        if (scratch is null) return;
        await scratch.Raw.InsertOneAsync(new BsonDocument
        {
            { "_id", 7 },
            { "Username", "legacy" },
            { "PasswordHash", "H:pw" },
            { "CreatedAt", DateTime.UtcNow.AddDays(-3) },
            { "IsBanned", false },
            { "AccessLevel", 0 },
            { "Characters", new BsonArray { "alpha", "beta" } },
        });

        var result = await AccountStoreHardeningScenarios.Handler(scratch.NewRepo())
            .HandleAsync(new RO2.Identity.Application.Commands.AuthenticateCommand("legacy", "pw"));

        Assert.True(result.Succeeded);
        var doc = await scratch.Raw.Find(new BsonDocument("_id", 7)).SingleAsync();
        Assert.Equal(2, doc["Characters"].AsBsonArray.Count);   // a whole-document replace would have dropped it
        Assert.True(doc.Contains("LastLogin") && !doc["LastLogin"].IsBsonNull);
    }
}
