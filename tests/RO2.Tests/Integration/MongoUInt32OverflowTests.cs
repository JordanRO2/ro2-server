using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using RO2.Persistence.Mongo.Persistence;
using Xunit;

namespace RO2.Tests.Integration;

// Regression net for the global lenient uint serializer, against a REAL mongo:7 container (MongoMailFixture).
// The driver's default UInt32 serializer wrote Int32 and threw OverflowException above int.MaxValue, which crashed saves of
// large char ids, dyes and ARGB colours. A uint now stays Int32 when it fits and is Int64 above it.

[Collection("MongoMail")]
public sealed class MongoUInt32OverflowTests
{
    private const string Db = "ro2_it_uint32_overflow";
    private const string Coll = "uint32_docs";
    private const uint Big = 4_000_000_000u;                                // above int.MaxValue
    private static long _seq = Random.Shared.Next(1_000_000, 1_000_000_000);

    private sealed class UIntDoc
    {
        [BsonId] public uint Id { get; set; }
        public uint Value { get; set; }
        public List<uint> Values { get; set; } = new();
    }

    private readonly MongoMailFixture _fx;
    public MongoUInt32OverflowTests(MongoMailFixture fx) { _fx = fx; }

    private (IMongoCollection<UIntDoc> typed, IMongoCollection<BsonDocument> raw) Open()
    {
        Assert.True(_fx.Available, "Docker is up but the mongo:7 fixture did not start: " + _fx.StartupError);
        var ctx = new MongoDbContext(_fx.ConnectionString, Db);          // its static ctor registers the provider
        return (ctx.GetCollection<UIntDoc>(Coll), _fx.Client!.GetDatabase(Db).GetCollection<BsonDocument>(Coll));
    }

    private static uint NewId() => (uint)Interlocked.Increment(ref _seq);

    [DockerFact]
    public async Task A_uint_above_int_max_saves_as_Int64_and_reads_back_exactly()
    {
        var (typed, raw) = Open();
        var id = NewId();
        await typed.InsertOneAsync(new UIntDoc { Id = id, Value = Big, Values = { Big, 5u } });   // used to throw OverflowException

        var stored = raw.Find(new BsonDocument("_id", (int)id)).Single();
        Assert.Equal(BsonType.Int64, stored["Value"].BsonType);
        Assert.Equal(BsonType.Int64, stored["Values"][0].BsonType);
        Assert.Equal(BsonType.Int32, stored["Values"][1].BsonType);
        var got = await typed.Find(d => d.Id == id).SingleAsync();
        Assert.Equal(Big, got.Value);
        Assert.Equal(new[] { Big, 5u }, got.Values);
    }

    [DockerFact]
    public async Task A_small_uint_is_still_stored_as_Int32()
    {
        var (typed, raw) = Open();
        var id = NewId();
        await typed.InsertOneAsync(new UIntDoc { Id = id, Value = 70 });
        await typed.InsertOneAsync(new UIntDoc { Id = NewId(), Value = int.MaxValue });

        var stored = raw.Find(new BsonDocument("_id", (int)id)).Single();
        Assert.Equal(BsonType.Int32, stored["_id"].BsonType);
        Assert.Equal(BsonType.Int32, stored["Value"].BsonType);
        Assert.Equal(70, stored["Value"].AsInt32);
        Assert.Equal(70u, (await typed.Find(d => d.Id == id).SingleAsync()).Value);
    }

    [DockerFact]
    public async Task An_Int32_stored_value_and_a_query_across_the_two_numeric_types_still_match()
    {
        var (typed, raw) = Open();
        var big = NewId();
        var small = NewId();
        var bigValue = Big - NewId() % 100_000_000;                      // unique per run: the collection is shared by the tests
        var smallValue = NewId();
        var legacyValue = NewId();
        await typed.InsertOneAsync(new UIntDoc { Id = big, Value = bigValue });
        await typed.InsertOneAsync(new UIntDoc { Id = small, Value = smallValue });
        await raw.InsertOneAsync(new BsonDocument { { "_id", (int)NewId() }, { "Value", (int)legacyValue } });   // a legacy Int32 document

        Assert.Equal(big, (await typed.Find(Builders<UIntDoc>.Filter.Eq(d => d.Value, bigValue)).SingleAsync()).Id);
        Assert.Equal(small, (await typed.Find(Builders<UIntDoc>.Filter.Eq(d => d.Value, smallValue)).SingleAsync()).Id);
        Assert.Equal(legacyValue, (await typed.Find(Builders<UIntDoc>.Filter.Eq(d => d.Value, legacyValue)).SingleAsync()).Value);
    }

    [DockerFact]
    public async Task A_uint_id_above_int_max_is_an_Int64_id_that_filters_and_replaces()
    {
        var (typed, raw) = Open();
        var id = Big - NewId() % 1000;                                   // random-ish id above int.MaxValue
        await typed.ReplaceOneAsync(d => d.Id == id, new UIntDoc { Id = id, Value = 1 }, new ReplaceOptions { IsUpsert = true });

        Assert.Equal(BsonType.Int64, raw.Find(new BsonDocument("_id", (long)id)).Single()["_id"].BsonType);
        Assert.Equal(1u, (await typed.Find(d => d.Id == id).SingleAsync()).Value);
    }

    [Fact]
    public void A_non_integer_BSON_value_is_a_clear_FormatException()
    {
        var doc = new BsonDocument { { "_id", 1 }, { "Value", "x" } };
        _ = new MongoDbContext("mongodb://localhost:1");                  // registers the provider; no connection is made
        var ex = Assert.ThrowsAny<Exception>(() => MongoDB.Bson.Serialization.BsonSerializer.Deserialize<UIntDoc>(doc));
        Assert.Contains("BSON type", ex.ToString());
    }
}
