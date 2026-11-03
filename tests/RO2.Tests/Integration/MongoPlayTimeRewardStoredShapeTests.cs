using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using RO2.Rewards.Domain;
using RO2.Rewards.Infrastructure.Mongo;
using RO2.Persistence.Mongo.Persistence;
using RO2.Persistence.Mongo.Repositories;
using Xunit;
using Xunit.Abstractions;

namespace RO2.Tests.Integration;

/// <summary>
/// B12 S1: the Mongo twin of <see cref="PostgresPlayTimeRewardTests"/>, against a REAL mongo:7 container (the <see cref="MongoMailFixture"/>
/// of the Mail twin, its own database). It pins the stored shape of the <c>account_playtime_reward</c> collection (the
/// <see cref="PlayTimeRewardDocument"/> itself is the stored document) and the replace-upsert / bulk-upsert behaviour of the Mongo
/// <see cref="PlayTimeRewardRepository"/>, so a byte-identical move of the document or the repository keeps it green. Every value below
/// was observed from the current code, not derived. Every test reports SKIPPED without Docker and asserts the fixture started when
/// Docker is up.
/// </summary>
[Collection("MongoMail")]
public sealed class MongoPlayTimeRewardStoredShapeTests
{
    private const string Db = "ro2_it_playtime_reward";
    private const string Coll = "account_playtime_reward";
    private static long _idSeq = Random.Shared.Next(1_000_000, 1_000_000_000);

    private readonly MongoMailFixture _fx;
    private readonly ITestOutputHelper _out;
    public MongoPlayTimeRewardStoredShapeTests(MongoMailFixture fx, ITestOutputHelper output) { _fx = fx; _out = output; }

    // Mongo's UInt32Serializer writes Int32 and throws above int.MaxValue (see Uint_ids_above_int_max_throw...), so every uint Id/SlotId
    // stays within int.MaxValue (the Postgres twin's ids up to uint.MaxValue do NOT work here).
    private static uint NewId() => (uint)Interlocked.Increment(ref _idSeq);

    private static PlayTimeRewardDocument Doc(uint id, uint slot, double seconds, DateTime period, DateTime updated)
        => new() { Id = id, SlotId = slot, AccumulatedSeconds = seconds, PeriodStartUtc = period, UpdatedUtc = updated };

    private static DateTime Utc(int year, int month, int day, int hour = 0, int minute = 0, int second = 0)
        => new(year, month, day, hour, minute, second, DateTimeKind.Utc);

    private PlayTimeRewardRepository Repo()
    {
        Assert.True(_fx.Available, "Docker is up but the mongo:7 fixture did not start: " + _fx.StartupError);
        return new PlayTimeRewardRepository(new MongoDbContext(_fx.ConnectionString, Db));
    }

    private IMongoCollection<BsonDocument> Raw()
        => _fx.Client!.GetDatabase(Db).GetCollection<BsonDocument>(Coll);

    private BsonDocument One(uint id) => Raw().Find(new BsonDocument("_id", (int)id)).Single();

    private long Count(uint id) => Raw().CountDocuments(new BsonDocument("_id", (int)id));

    // ------------------------------------------------------------------------------------------------ proof it ran

    [DockerFact]
    public async Task Ran_against_a_real_mongo7_container()
    {
        Assert.True(_fx.Available, "Docker is up but the mongo:7 fixture did not start: " + _fx.StartupError);
        var info = await _fx.Client!.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument("buildInfo", 1));
        var version = info["version"].AsString;
        _out.WriteLine($"MongoPlayTimeRewardStoredShapeTests: MongoMailFixture.Available=True mongod={version} at {_fx.ConnectionString}");
        Assert.StartsWith("7.", version);
    }

    // ------------------------------------------------------------------------------------------------ stored shape

    [DockerFact]
    public async Task The_document_shape_is_pinned_and_round_trips_every_field()
    {
        var repo = Repo();
        var id = NewId();
        var period = Utc(2026, 10, 18, 5);
        var updated = Utc(2026, 10, 18, 7, 30, 15);

        await repo.SaveDocumentAsync(Doc(id, 123, 3600.75, period, updated));

        var raw = One(id);
        Assert.Equal(new[] { "_id", "SlotId", "AccumulatedSeconds", "PeriodStartUtc", "UpdatedUtc" }, raw.Names.ToArray());   // Id -> _id, declaration order
        Assert.Equal(BsonType.Int32, raw["_id"].BsonType);                          // uint -> Int32
        Assert.Equal((int)id, raw["_id"].AsInt32);
        Assert.Equal(BsonType.Int32, raw["SlotId"].BsonType);                       // uint -> Int32
        Assert.Equal(123, raw["SlotId"].AsInt32);
        Assert.Equal(BsonType.Double, raw["AccumulatedSeconds"].BsonType);
        Assert.Equal(3600.75, raw["AccumulatedSeconds"].AsDouble);
        Assert.Equal(BsonType.DateTime, raw["PeriodStartUtc"].BsonType);
        Assert.Equal(period, raw["PeriodStartUtc"].ToUniversalTime());
        Assert.Equal(BsonType.DateTime, raw["UpdatedUtc"].BsonType);
        Assert.Equal(updated, raw["UpdatedUtc"].ToUniversalTime());
        Assert.DoesNotContain("\"_t\"", raw.ToJson());                              // no type discriminator

        var got = await repo.GetDocumentAsync(id);
        Assert.NotNull(got);
        Assert.Equal(id, got.Id);
        Assert.Equal(123u, got.SlotId);
        Assert.Equal(3600.75, got.AccumulatedSeconds);
        Assert.Equal(period, got.PeriodStartUtc);
        Assert.Equal(DateTimeKind.Utc, got.PeriodStartUtc.Kind);
        Assert.Equal(updated, got.UpdatedUtc);
        Assert.Equal(DateTimeKind.Utc, got.UpdatedUtc.Kind);
    }

    [DockerFact]
    public async Task A_missing_account_is_null_and_a_read_stores_nothing()
    {
        var repo = Repo();
        var id = NewId();

        Assert.Null(await repo.GetDocumentAsync(id));
        Assert.Equal(0, Count(id));
    }

    [DockerFact]
    public async Task Save_replaces_the_whole_document_without_duplicating_and_accounts_are_independent()
    {
        var repo = Repo();
        var id = NewId();
        await repo.SaveDocumentAsync(Doc(id, 10, 100, Utc(2026, 10, 1), Utc(2026, 10, 1, 1)));
        await repo.SaveDocumentAsync(Doc(id, 20, 250.25, Utc(2026, 10, 2), Utc(2026, 10, 2, 2)));

        var got = await repo.GetDocumentAsync(id);
        Assert.NotNull(got);
        Assert.Equal(20u, got.SlotId);
        Assert.Equal(250.25, got.AccumulatedSeconds);
        Assert.Equal(Utc(2026, 10, 2), got.PeriodStartUtc);
        Assert.Equal(Utc(2026, 10, 2, 2), got.UpdatedUtc);
        Assert.Equal(1, Count(id));
        Assert.Equal(new[] { "_id", "SlotId", "AccumulatedSeconds", "PeriodStartUtc", "UpdatedUtc" }, One(id).Names.ToArray());

        var other = NewId();
        await repo.SaveDocumentAsync(Doc(other, 2, 22, Utc(2026, 10, 1), Utc(2026, 10, 1)));
        await repo.SaveDocumentAsync(Doc(id, 3, 33, Utc(2026, 10, 1), Utc(2026, 10, 1)));
        Assert.Equal(3u, (await repo.GetDocumentAsync(id))!.SlotId);
        Assert.Equal(2u, (await repo.GetDocumentAsync(other))!.SlotId);
    }

    [DockerFact]
    public async Task SaveMany_empty_is_a_noop_inserts_new_and_replaces_existing_and_the_last_duplicate_wins()
    {
        var repo = Repo();
        await repo.SaveManyDocumentsAsync(Array.Empty<PlayTimeRewardDocument>());

        var existing = NewId();
        var fresh1 = NewId();
        var fresh2 = NewId();
        await repo.SaveDocumentAsync(Doc(existing, 1, 5, Utc(2026, 10, 1), Utc(2026, 10, 1)));

        await repo.SaveManyDocumentsAsync(new[]
        {
            Doc(existing, 9, 90.5, Utc(2026, 10, 5), Utc(2026, 10, 5, 1)),
            Doc(fresh1, 7, 70, Utc(2026, 10, 5), Utc(2026, 10, 5, 2)),
            Doc(fresh2, 8, 80, Utc(2026, 10, 5), Utc(2026, 10, 5, 3)),
        });

        var e = await repo.GetDocumentAsync(existing);
        Assert.Equal(9u, e!.SlotId);
        Assert.Equal(90.5, e.AccumulatedSeconds);
        Assert.Equal(Utc(2026, 10, 5, 1), e.UpdatedUtc);
        Assert.Equal(7u, (await repo.GetDocumentAsync(fresh1))!.SlotId);
        Assert.Equal(Utc(2026, 10, 5, 3), (await repo.GetDocumentAsync(fresh2))!.UpdatedUtc);
        Assert.Equal(1, Count(existing));
        Assert.Equal(new[] { "_id", "SlotId", "AccumulatedSeconds", "PeriodStartUtc", "UpdatedUtc" }, One(fresh1).Names.ToArray());   // the bulk replace stores the same shape

        var dup = NewId();
        await repo.SaveManyDocumentsAsync(new[]
        {
            Doc(dup, 1, 10, Utc(2026, 10, 1), Utc(2026, 10, 1, 1)),
            Doc(dup, 2, 20, Utc(2026, 10, 1), Utc(2026, 10, 1, 2)),
        });
        var got = await repo.GetDocumentAsync(dup);
        Assert.Equal(2u, got!.SlotId);
        Assert.Equal(20, got.AccumulatedSeconds);
        Assert.Equal(1, Count(dup));
    }

    // ------------------------------------------------------------------------------------------------ the uint -> Int32 bound

    [DockerFact]
    public async Task Uint_ids_above_int_max_throw_and_int_max_is_the_last_good_id_while_AccumulatedSeconds_is_an_unbounded_double()
    {
        var repo = Repo();

        // int.MaxValue round-trips as an Int32 (account id in _id, slot id in the document); AccumulatedSeconds is a Double, not a uint.
        var okId = (uint)int.MaxValue;
        await repo.SaveDocumentAsync(Doc(okId, int.MaxValue, 5_000_000_000d, Utc(2026, 10, 1), Utc(2026, 10, 1)));
        var raw = Raw().Find(new BsonDocument("_id", int.MaxValue)).Single();
        Assert.Equal(BsonType.Int32, raw["_id"].BsonType);
        Assert.Equal(int.MaxValue, raw["SlotId"].AsInt32);
        Assert.Equal(5_000_000_000d, raw["AccumulatedSeconds"].AsDouble);
        var got = await repo.GetDocumentAsync(okId);
        Assert.Equal((uint)int.MaxValue, got!.SlotId);
        Assert.Equal(5_000_000_000d, got.AccumulatedSeconds);

        // A slot id above it sits in the DOCUMENT: BsonSerializationException wrapping the OverflowException, nothing stored.
        var id = NewId();
        var bad = await Assert.ThrowsAsync<BsonSerializationException>(
            () => repo.SaveDocumentAsync(Doc(id, 3_000_000_000u, 1, Utc(2026, 10, 1), Utc(2026, 10, 1))));
        var inner = bad.InnerException;
        while (inner is BsonSerializationException) inner = inner.InnerException;
        Assert.IsType<OverflowException>(inner);
        Assert.Equal(0, Count(id));

        // An account id above it sits in the FILTER: the raw OverflowException, on a read and on a write alike.
        await Assert.ThrowsAsync<OverflowException>(() => repo.GetDocumentAsync(3_000_000_000u));
        await Assert.ThrowsAsync<OverflowException>(
            () => repo.SaveDocumentAsync(Doc(3_000_000_000u, 1, 1, Utc(2026, 10, 1), Utc(2026, 10, 1))));
    }
}
