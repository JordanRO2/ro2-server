using RO2.Application.Interfaces;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using RO2.Persistence.Mongo.Persistence;
using Xunit;
using Xunit.Abstractions;

namespace RO2.Tests.Services;

/// <summary>
/// Unit tests for the one-shot token store that need no database: record shape, the exact <c>findOneAndDelete</c>
/// filter, expiry arithmetic, the TTL index model, and that the constructor does no I/O. Tests that need a server
/// (atomic consumption under races and the TTL monitor) are in <see cref="OneShotTokenStoreMongoIntegrationTests"/>.
/// </summary>
public sealed class OneShotTokenStoreTests
{
    private static readonly DateTime Now = new(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);

    private static BsonDocument Render(FilterDefinition<OneShotTokenStore.TokenDocument> filter)
        => filter.Render(new RenderArgs<OneShotTokenStore.TokenDocument>(
            BsonSerializer.SerializerRegistry.GetSerializer<OneShotTokenStore.TokenDocument>(),
            BsonSerializer.SerializerRegistry)).AsBsonDocument;

    [Fact]
    public void NewToken_Is128BitHex_AndUnique()
    {
        var a = OneShotTokens.NewToken();
        var b = OneShotTokens.NewToken();
        Assert.Equal(OneShotTokens.RandomTokenBytes * 2, a.Length);
        Assert.Matches("^[0-9A-F]{32}$", a);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Document_CarriesTheIssuedIdentityAndExpiry()
    {
        var doc = OneShotTokenStore.ToDocument("T", OneShotTokens.PurposeMessenger, 1679652u, 123456789012ul,
            Now, TimeSpan.FromSeconds(30));

        Assert.Equal("T", doc.Id);
        Assert.Equal("messenger", doc.Purpose);
        Assert.Equal(1679652L, doc.AccountId);
        Assert.Equal(123456789012L, doc.CharacterUid);
        Assert.Equal(Now, doc.IssuedUtc);
        Assert.Equal(Now.AddSeconds(30), doc.Expiry);
    }

    [Fact]
    public void Token_RoundTripsFullRangeIds()
    {
        var doc = OneShotTokenStore.ToDocument("T", "zone", uint.MaxValue, ulong.MaxValue, Now, TimeSpan.FromMinutes(1));
        var token = OneShotTokenStore.ToToken(doc);

        Assert.Equal(new OneShotToken("T", "zone", uint.MaxValue, ulong.MaxValue, Now, Now.AddMinutes(1)), token);
    }

    [Fact]
    public void Document_BsonShape_IdIsTheToken_ExpiryIsADate()
    {
        var doc = OneShotTokenStore.ToDocument("TOK", "zone", 7u, 9ul, Now, TimeSpan.FromSeconds(30));
        var bson = doc.ToBsonDocument();

        Assert.Equal("TOK", bson["_id"].AsString);
        Assert.Equal(BsonType.DateTime, bson["Expiry"].BsonType);
        Assert.Equal("zone", bson["Purpose"].AsString);
        Assert.Equal(7L, bson["AccountId"].ToInt64());
        Assert.Equal(9L, bson["CharacterUid"].ToInt64());
    }

    [Fact]
    public void ConsumeFilter_MatchesTokenPurposeAndUnexpired()
    {
        var f = Render(OneShotTokenStore.ConsumeFilter("TOK", "zone", Now));

        
        var text = f.ToJson();
        Assert.Contains("\"_id\"", text);
        Assert.Contains("TOK", text);
        Assert.Contains("\"Purpose\"", text);
        Assert.Contains("zone", text);
        Assert.Contains("\"Expiry\"", text);
        Assert.Contains("$gt", text);
        Assert.DoesNotContain("AccountId", text);
    }

    [Fact]
    public void ConsumeFilter_WithAccount_AddsTheAccountCondition_SoAWrongAccountDeletesNothing()
    {
        var text = Render(OneShotTokenStore.ConsumeFilter("TOK", "zone", Now, accountId: 42u)).ToJson();
        Assert.Contains("AccountId", text);
        Assert.Contains("42", text);
    }

    [Fact]
    public void ConsumeFilter_ExpiryBoundIsStrict()
    {
        var f = Render(OneShotTokenStore.ConsumeFilter("TOK", "zone", Now));
        var expiry = FindExpiryCondition(f);

        
        Assert.Equal(Now, expiry["$gt"].ToUniversalTime());
    }

    private static BsonDocument FindExpiryCondition(BsonDocument filter)
    {
        if (filter.TryGetValue("Expiry", out var direct) && direct.IsBsonDocument)
            return direct.AsBsonDocument;
        foreach (var clause in filter["$and"].AsBsonArray)
            if (clause.AsBsonDocument.TryGetValue("Expiry", out var e))
                return e.AsBsonDocument;
        throw new Xunit.Sdk.XunitException("no Expiry condition in " + filter.ToJson());
    }

    [Fact]
    public void ExpiryIndex_IsATtlIndexOnExpiry_WithZeroGrace()
    {
        var model = OneShotTokenStore.ExpiryIndexModel();
        var keys = model.Keys.Render(new RenderArgs<OneShotTokenStore.TokenDocument>(
            BsonSerializer.SerializerRegistry.GetSerializer<OneShotTokenStore.TokenDocument>(),
            BsonSerializer.SerializerRegistry)).AsBsonDocument;

        Assert.Equal(1, keys["Expiry"].AsInt32);
        Assert.Equal(TimeSpan.Zero, model.Options.ExpireAfter);
        Assert.Equal(OneShotTokenStore.ExpiryIndexName, model.Options.Name);
    }

    [Fact]
    public async Task EmptyTokenOrPurpose_IsRefusedWithoutTouchingMongo()
    {
        
        
        var store = new OneShotTokenStore(new MongoDbContext("mongodb://127.0.0.1:1/?serverSelectionTimeoutMS=200"));

        Assert.Null(await store.ConsumeAsync("", "zone"));
        Assert.Null(await store.ConsumeAsync("TOK", ""));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => store.IssueAsync("TOK", "zone", 1, 1, TimeSpan.Zero));
        await Assert.ThrowsAsync<ArgumentException>(
            () => store.IssueAsync("", "zone", 1, 1, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task EnsureIndexesAsync_WhenMongoIsDown_ReturnsFalseInsteadOfThrowing()
    {
        var store = new OneShotTokenStore(new MongoDbContext("mongodb://127.0.0.1:1/?serverSelectionTimeoutMS=300"));
        Assert.False(await store.EnsureIndexesAsync());
    }
}

/// <summary>
/// Integration tests for the MongoDB guarantees: issue, consume, and a second consume that returns null;
/// exactly-once consumption under concurrency; expiry and the TTL index. Runs against a throwaway database on
/// <c>localhost:27017</c> that is dropped afterwards, and is a no-op when MongoDB is unreachable, like
/// <c>InventoryRepositoryMongoIntegrationTests</c>.
/// </summary>
public sealed class OneShotTokenStoreMongoIntegrationTests
{
    private const string ConnectionString = "mongodb://localhost:27017";

    private readonly ITestOutputHelper _output;

    public OneShotTokenStoreMongoIntegrationTests(ITestOutputHelper output) => _output = output;

    private bool MongoUp()
    {
        try
        {
            var settings = MongoClientSettings.FromConnectionString(ConnectionString);
            settings.ServerSelectionTimeout = TimeSpan.FromSeconds(2);
            _ = new MongoClient(settings).ListDatabaseNames(new ListDatabaseNamesOptions());
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or MongoException)
        {
            _output.WriteLine($"SKIP: Mongo unreachable at {ConnectionString} ({ex.GetType().Name}).");
            return false;
        }
    }

    private sealed class FakeTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private static async Task WithFreshDatabase(Func<MongoDbContext, Task> body)
    {
        var dbName = "ro2_test_tok_" + Guid.NewGuid().ToString("N");
        try
        {
            await body(new MongoDbContext(ConnectionString, dbName));
        }
        finally
        {
            await new MongoClient(ConnectionString).DropDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task IssueConsume_ReturnsTheIdentity_ThenSecondConsumeIsNull()
    {
        if (!MongoUp()) return;

        await WithFreshDatabase(async db =>
        {
            var store = new OneShotTokenStore(db);
            await store.EnsureIndexesAsync();

            var token = await store.IssueNewAsync(OneShotTokens.PurposeMessenger, 1679652u, 77ul, TimeSpan.FromSeconds(30));

            var first = await store.ConsumeAsync(token, OneShotTokens.PurposeMessenger, 1679652u);
            Assert.NotNull(first);
            Assert.Equal(1679652u, first.AccountId);
            Assert.Equal(77ul, first.CharacterUid);
            Assert.Equal(OneShotTokens.PurposeMessenger, first.Purpose);

            Assert.Null(await store.ConsumeAsync(token, OneShotTokens.PurposeMessenger, 1679652u));
        });
    }

    [Fact]
    public async Task ConcurrentConsume_OfOneToken_SucceedsExactlyOnce()
    {
        if (!MongoUp()) return;

        await WithFreshDatabase(async db =>
        {
            var store = new OneShotTokenStore(db);
            var token = await store.IssueNewAsync(OneShotTokens.PurposeZone, 5u, 6ul, TimeSpan.FromSeconds(30));

            var results = await Task.WhenAll(Enumerable.Range(0, 24)
                .Select(_ => store.ConsumeAsync(token, OneShotTokens.PurposeZone)));

            Assert.Equal(1, results.Count(r => r is not null));
        });
    }

    [Fact]
    public async Task WrongPurposeOrAccount_ReturnsNull_AndDoesNotBurnTheToken()
    {
        if (!MongoUp()) return;

        await WithFreshDatabase(async db =>
        {
            var store = new OneShotTokenStore(db);
            var token = await store.IssueNewAsync(OneShotTokens.PurposeZone, 5u, 6ul, TimeSpan.FromSeconds(30));

            Assert.Null(await store.ConsumeAsync(token, OneShotTokens.PurposeMessenger));
            Assert.Null(await store.ConsumeAsync(token, OneShotTokens.PurposeZone, accountId: 999u));
            Assert.NotNull(await store.ConsumeAsync(token, OneShotTokens.PurposeZone, accountId: 5u));
        });
    }

    [Fact]
    public async Task ExpiredToken_IsNotConsumable_EvenBeforeTheTtlMonitorRuns()
    {
        if (!MongoUp()) return;

        await WithFreshDatabase(async db =>
        {
            var time = new FakeTime(DateTimeOffset.UtcNow);
            var store = new OneShotTokenStore(db, time);
            var token = await store.IssueNewAsync(OneShotTokens.PurposeZone, 5u, 6ul, TimeSpan.FromSeconds(30));

            time.Advance(TimeSpan.FromSeconds(29));
            
            Assert.Null(await store.ConsumeAsync(token, OneShotTokens.PurposeMessenger));

            time.Advance(TimeSpan.FromSeconds(2)); 
            Assert.Null(await store.ConsumeAsync(token, OneShotTokens.PurposeZone));
        });
    }

    [Fact]
    public async Task DuplicateToken_IsRefusedAndKeepsTheFirstIdentity()
    {
        if (!MongoUp()) return;

        await WithFreshDatabase(async db =>
        {
            var store = new OneShotTokenStore(db);
            Assert.True(await store.IssueAsync("SAME", "zone", 1u, 1ul, TimeSpan.FromSeconds(30)));
            Assert.False(await store.IssueAsync("SAME", "zone", 2u, 2ul, TimeSpan.FromSeconds(30)));

            var got = await store.ConsumeAsync("SAME", "zone");
            Assert.Equal(1u, got!.AccountId);
        });
    }

    [Fact]
    public async Task EnsureIndexes_CreatesTheTtlIndex_AndIsIdempotent()
    {
        if (!MongoUp()) return;

        await WithFreshDatabase(async db =>
        {
            var store = new OneShotTokenStore(db);
            Assert.True(await store.EnsureIndexesAsync());
            Assert.True(await store.EnsureIndexesAsync());

            var indexes = await (await db.GetCollection<BsonDocument>(OneShotTokenStore.CollectionName)
                .Indexes.ListAsync()).ToListAsync();
            var ttl = indexes.Single(i => i["name"].AsString == OneShotTokenStore.ExpiryIndexName);
            Assert.Equal(0, ttl["expireAfterSeconds"].ToInt64());
            Assert.Equal(1, ttl["key"]["Expiry"].AsInt32);
        });
    }
}
