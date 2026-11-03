using MongoDB.Bson;
using MongoDB.Driver;
using RO2.Persistence.Mongo.Persistence;
using RO2.Chats.Infrastructure.Mongo;
using Xunit;
using Xunit.Abstractions;

namespace RO2.Tests.Integration;

// B15 S1: the stored-shape net for chat_channel_colors against a REAL mongo:7 container (MongoMailFixture, one database per class).
// ChannelColorDocument is private, so the shape is pinned through raw BsonDocuments written/read around the concrete repository.

[Collection("MongoMail")]
public sealed class MongoChannelColorStoredShapeTests
{
    private const string Db = "ro2_it_chat_channel_colors";
    private static long _seq = Random.Shared.Next(1_000_000, 1_000_000_000);

    private readonly MongoMailFixture _fx;
    private readonly ITestOutputHelper _out;
    public MongoChannelColorStoredShapeTests(MongoMailFixture fx, ITestOutputHelper output) { _fx = fx; _out = output; }

    // uint is Int32 up to int.MaxValue and Int64 above (LenientUInt32Serializer); owner ids stay small so One() can look them up as Int32.
    private static uint New() => (uint)Interlocked.Increment(ref _seq);

    private ChannelColorRepository Open()
    {
        Assert.True(_fx.Available, "Docker is up but the mongo:7 fixture did not start: " + _fx.StartupError);
        return new ChannelColorRepository(new MongoDbContext(_fx.ConnectionString, Db));
    }

    private IMongoCollection<BsonDocument> Raw()
        => _fx.Client!.GetDatabase(Db).GetCollection<BsonDocument>(ChannelColorRepository.CollectionName);
    private BsonDocument One(uint id) => Raw().Find(new BsonDocument("_id", (int)id)).Single();

    [DockerFact]
    public async Task Ran_against_a_real_mongo7_container()
    {
        Assert.True(_fx.Available, "Docker is up but the mongo:7 fixture did not start: " + _fx.StartupError);
        var info = await _fx.Client!.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument("buildInfo", 1));
        var version = info["version"].AsString;
        _out.WriteLine($"MongoChannelColorStoredShapeTests: MongoMailFixture.Available=True mongod={version} at {_fx.ConnectionString}");
        Assert.StartsWith("7.", version);
        Assert.Equal("chat_channel_colors", ChannelColorRepository.CollectionName);
    }

    [DockerFact]
    public async Task A_set_color_is_stored_as_Int32_id_and_a_Colors_array_of_Index_Argb_with_no_discriminator()
    {
        var repo = Open();
        var owner = New();
        await repo.SetColorAsync(owner, 12, 0x00112233u);
        await repo.SetColorAsync(owner, 14, 0x00445566u);

        var raw = One(owner);
        Assert.Equal(new[] { "_id", "Colors" }, raw.Names.ToArray());
        Assert.Equal(BsonType.Int32, raw["_id"].BsonType);
        Assert.Equal((int)owner, raw["_id"].AsInt32);
        Assert.Equal(BsonType.Array, raw["Colors"].BsonType);
        var colors = raw["Colors"].AsBsonArray;
        Assert.Equal(2, colors.Count);
        Assert.All(colors, v =>
        {
            var e = v.AsBsonDocument;
            Assert.Equal(new[] { "Index", "Argb" }, e.Names.ToArray());
            Assert.Equal(BsonType.Int32, e["Index"].BsonType);
            Assert.Equal(BsonType.Int32, e["Argb"].BsonType);
        });
        // Order is insertion order (push), not index order.
        Assert.Equal(new[] { 12, 14 }, colors.Select(v => v["Index"].AsInt32).ToArray());
        Assert.Equal(new[] { 0x00112233, 0x00445566 }, colors.Select(v => v["Argb"].AsInt32).ToArray());
        Assert.DoesNotContain("\"_t\"", raw.ToJson());
        Assert.Equal(1, Raw().CountDocuments(new BsonDocument("_id", (int)owner)));
    }

    [DockerFact]
    public async Task Set_get_overwrite_round_trips_in_place_without_adding_an_element()
    {
        var repo = Open();
        var owner = New();
        await repo.SetColorAsync(owner, 13, 1u);
        await repo.SetColorAsync(owner, 13, 2u);
        await repo.SetColorAsync(owner, 12, 3u);
        await repo.SetColorAsync(owner, 13, 4u);

        var colors = await repo.GetColorsAsync(owner);
        Assert.Equal(2, colors.Count);
        Assert.Equal(4u, colors[13]);
        Assert.Equal(3u, colors[12]);
        var arr = One(owner)["Colors"].AsBsonArray;
        Assert.Equal(new[] { 13, 12 }, arr.Select(v => v["Index"].AsInt32).ToArray());   // 13 was overwritten in place, 12 pushed after
    }

    [DockerFact]
    public async Task Unknown_owner_and_legacy_or_empty_documents_read_as_no_colors_and_extra_elements_are_ignored()
    {
        var repo = Open();
        Assert.Empty(await repo.GetColorsAsync(New()));

        var empty = New();
        await Raw().InsertOneAsync(new BsonDocument { { "_id", (int)empty }, { "Colors", new BsonArray() } });
        Assert.Empty(await repo.GetColorsAsync(empty));

        var legacy = New();   // no Colors field at all, plus an element the document type does not know
        await Raw().InsertOneAsync(new BsonDocument { { "_id", (int)legacy }, { "Legacy", "x" } });
        Assert.Empty(await repo.GetColorsAsync(legacy));

        // Setting on an empty-array document pushes into it (no upsert collision).
        await repo.SetColorAsync(empty, 14, 7u);
        Assert.Equal(7u, (await repo.GetColorsAsync(empty))[14]);
    }

    [DockerFact]
    public async Task Argb_and_owner_are_stored_as_Int32_and_a_value_above_int_max_is_stored_as_Int64()
    {
        var repo = Open();
        var owner = New();
        await repo.SetColorAsync(owner, ushort.MaxValue, int.MaxValue);
        await repo.SetColorAsync(owner, 0, 0u);
        var colors = await repo.GetColorsAsync(owner);
        Assert.Equal((uint)int.MaxValue, colors[ushort.MaxValue]);
        Assert.Equal(0u, colors[0]);
        Assert.Equal(new[] { 65535, 0 }, One(owner)["Colors"].AsBsonArray.Select(v => v["Index"].AsInt32).ToArray());

        // Opaque 0xFFxxxxxx colours and uint.MaxValue are stored as Int64, like Postgres bigint, and read back exactly.
        var wide = New();
        await repo.SetColorAsync(wide, 12, 0xFF112233u);
        await repo.SetColorAsync(wide, 13, uint.MaxValue);
        var wideColors = await repo.GetColorsAsync(wide);
        Assert.Equal(0xFF112233u, wideColors[12]);
        Assert.Equal(uint.MaxValue, wideColors[13]);
        Assert.All(One(wide)["Colors"].AsBsonArray, v => Assert.Equal(BsonType.Int64, v["Argb"].BsonType));
    }
}
