using MongoDB.Bson;
using MongoDB.Driver;
using RO2.Persistence.Mongo.Persistence;
using RO2.Persistence.Mongo.Repositories;
using Xunit;

namespace RO2.Tests.Integration;

/// <summary>
/// S-H8, store side, on the Mongo provider: the <c>ExpiresAt</c> TTL index and one live token per account on
/// issue. Runs in a scratch database that is dropped afterwards, and soft-skips (green no-op) when Mongo is
/// unreachable. Point it at another server with <c>RO2_TEST_MONGO</c> (default <c>mongodb://localhost:27017</c>).
/// </summary>
public sealed class MongoZoneAuthTokenHardeningTests
{
    private sealed class Scratch : IDisposable
    {
        private readonly MongoClient _client;
        public string Database { get; } = "ro2_it_" + Guid.NewGuid().ToString("N");
        public ZoneAuthTokenRepository Repo { get; }

        public Scratch(MongoClient client, string connectionString)
        {
            _client = client;
            Repo = new ZoneAuthTokenRepository(new MongoDbContext(connectionString, Database));
        }

        public IMongoCollection<BsonDocument> Raw => _client.GetDatabase(Database).GetCollection<BsonDocument>("zone_auth_tokens");

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
    public async Task Ensure_indexes_creates_the_expires_at_ttl_index()
    {
        using var scratch = TryScratch();
        if (scratch is null) return;

        Assert.True(await scratch.Repo.EnsureIndexesAsync());

        var indexes = await (await scratch.Raw.Indexes.ListAsync()).ToListAsync();
        var ttl = indexes.Single(i => i["name"].AsString == ZoneAuthTokenRepository.ExpiryIndexName);
        Assert.Equal(0, ttl["expireAfterSeconds"].ToInt32());
        Assert.Equal(1, ttl["key"]["ExpiresAt"].ToInt32());
    }

    [Fact]
    public async Task Issuing_twice_for_one_account_leaves_one_live_token()
    {
        using var scratch = TryScratch();
        if (scratch is null) return;
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        await scratch.Repo.IssueAsync(first, 4242, TimeSpan.FromMinutes(5));
        await Task.Delay(20); // the later issue must carry the later expiry
        await scratch.Repo.IssueAsync(second, 4242, TimeSpan.FromMinutes(5));

        Assert.Equal(1, await scratch.Raw.CountDocumentsAsync(new BsonDocument("AccountId", 4242)));
        Assert.Null(await scratch.Repo.ConsumeAsync(first));
        Assert.Equal(4242u, await scratch.Repo.ConsumeAsync(second));
    }

    [Fact]
    public async Task Issuing_for_one_account_leaves_another_accounts_token_alone()
    {
        using var scratch = TryScratch();
        if (scratch is null) return;
        var tokenA = Guid.NewGuid();

        await scratch.Repo.IssueAsync(tokenA, 1, TimeSpan.FromMinutes(5));
        await scratch.Repo.IssueAsync(Guid.NewGuid(), 2, TimeSpan.FromMinutes(5));

        Assert.Equal(1u, await scratch.Repo.ConsumeAsync(tokenA));
    }
}
