using MongoDB.Bson;
using MongoDB.Driver;
using RO2.Chats.Domain;
using RO2.Chats.Infrastructure.Mongo;
using RO2.Persistence.Mongo.Persistence;
using Xunit;

namespace RO2.Tests.Integration;

/// <summary>
/// [41] The Mongo close-range signal bus orders by a stored cross-process sequence, not by ObjectId (whose seconds-resolution
/// timestamp plus per-process random leaves the Zone's open and the Chat's exit unordered within one second). Same contract as the
/// Redis stream id, whose twin is <see cref="RedisCloseRangeRoomSignalsTests"/>.
/// </summary>
[Collection("MongoMail")]
public sealed class MongoCloseRangeRoomSignalOrderTests
{
    private const string Db = "ro2_it_signal_order";
    private readonly MongoMailFixture _fx;

    public MongoCloseRangeRoomSignalOrderTests(MongoMailFixture fx) => _fx = fx;

    private static BsonDocument Doc(ObjectId id, long seq, string kind, byte[] room, uint character, uint newMaster = 0)
        => new()
        {
            { "_id", id }, { "Seq", seq }, { "Kind", kind }, { "RoomGuid", new BsonBinaryData(room) },
            { "CharacterId", (long)character }, { "Capacity", 5 }, { "Password", new BsonBinaryData(Array.Empty<byte>()) },
            { "MemberCount", 0 }, { "NewMasterCharacterId", (long)newMaster }, { "OwnerGone", newMaster == 0 },
            { "CreatedUtc", DateTime.UtcNow },
        };

    [DockerFact]
    public async Task An_exit_inserted_after_an_open_is_seen_even_when_its_ObjectId_sorts_before_it()
    {
        Assert.True(_fx.Available, "Docker is up but the mongo:7 fixture did not start: " + _fx.StartupError);
        var ctx = new MongoDbContext(_fx.ConnectionString, Db);
        var bus = new CloseRangeRoomSignalBus(ctx);
        await bus.PublishJoinAsync(new byte[16], 1, 1); // creates the capped collection
        var raw = _fx.Client!.GetDatabase(Db).GetCollection<BsonDocument>(CloseRangeRoomSignalBus.CollectionName);
        var room = Guid.NewGuid().ToByteArray();
        var seq = (await raw.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty)) + 1000;

        // Same second, different "process" random bytes: the owner's exit (later) carries the smaller ObjectId.
        var openId = ObjectId.Parse("6700000100000000ff000001");
        var exitId = ObjectId.Parse("6700000100000000aa000001");
        await raw.InsertOneAsync(Doc(openId, seq + 1, CloseRangeRoomSignals.OpenKind, room, 77));
        await raw.InsertOneAsync(Doc(exitId, seq + 2, CloseRangeRoomSignals.ExitKind, room, 77));

        Assert.Null(await bus.FindOpenRoomAsync(room)); // the owner left with no hand-off: closed
        var history = await bus.GetRoomHistoryAsync(room);
        Assert.NotNull(history);
        Assert.Equal(CloseRangeRoomSignals.ExitKind, Assert.Single(history!.Later).Kind);
    }

    [DockerFact]
    public async Task Published_signals_get_increasing_ids_across_bus_instances()
    {
        Assert.True(_fx.Available, "Docker is up but the mongo:7 fixture did not start: " + _fx.StartupError);
        var a = new CloseRangeRoomSignalBus(new MongoDbContext(_fx.ConnectionString, Db));
        var b = new CloseRangeRoomSignalBus(new MongoDbContext(_fx.ConnectionString, Db));
        var room = Guid.NewGuid().ToByteArray();
        await a.PublishOpenAsync(room, 9, 5, default);
        await b.PublishExitAsync(room, 9, 0, 4, false);
        await a.PublishExitAsync(room, 4, 0, 0, true);

        var history = await a.GetRoomHistoryAsync(room);
        Assert.NotNull(history);
        Assert.Equal(2, history!.Later.Count);
        Assert.True(string.CompareOrdinal(history.Open.Id, history.Later[0].Id) < 0);
        Assert.True(string.CompareOrdinal(history.Later[0].Id, history.Later[1].Id) < 0);
        Assert.Null(await a.FindOpenRoomAsync(room)); // 9 handed to 4, then 4 left with no hand-off
    }
}
