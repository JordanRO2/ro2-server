using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using RO2.Domain.Entities;
using RO2.Kharas.Domain;
using RO2.Persistence.Mongo.Persistence;
using RO2.Persistence.Mongo.Repositories;
using RO2.Server.Zone.Services;
using Xunit;
using Xunit.Abstractions;

namespace RO2.Tests.Integration;

// B13 S1: the Mongo twins of PostgresKharaStateTests / PostgresKharaTitleTests / PostgresKharaTraceTests, against a REAL mongo:7
// container (the MongoMailFixture of the Mail twin, one database per class). They pin the stored shape of the three Khara collections
// (character_khara, character_khara_title, khara_traces) and the rules of the Mongo repositories, so a byte-identical move of the
// entities or the repositories keeps them green. Every value below was observed from the current code, not derived. Every test
// reports SKIPPED without Docker and asserts the fixture started when Docker is up.

/// <summary>The <c>character_khara</c> collection: <see cref="KharaStateDocument"/> with its nested <see cref="KharaActiveDocument"/> elements.</summary>
[Collection("MongoMail")]
public sealed class MongoKharaStateStoredShapeTests
{
    private const string Db = "ro2_it_khara_state";
    private const string Coll = "character_khara";
    private static readonly DateTime Stamp = new(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
    private static long _charSeq = Random.Shared.Next(1_000_000, 1_000_000_000);

    private readonly MongoMailFixture _fx;
    private readonly ITestOutputHelper _out;
    public MongoKharaStateStoredShapeTests(MongoMailFixture fx, ITestOutputHelper output) { _fx = fx; _out = output; }

    // Mongo's UInt32Serializer writes Int32 and throws above int.MaxValue (see Uint_values_above_int_max_throw...), so every uint stored
    // value stays within int.MaxValue (the Postgres twin's 2.2e9..4.2e9 character ids do NOT work here).
    private static uint NewChar() => (uint)Interlocked.Increment(ref _charSeq);

    private KharaStateRepository Repo()
    {
        Assert.True(_fx.Available, "Docker is up but the mongo:7 fixture did not start: " + _fx.StartupError);
        return new KharaStateRepository(new MongoDbContext(_fx.ConnectionString, Db));
    }

    private IMongoCollection<BsonDocument> Raw() => _fx.Client!.GetDatabase(Db).GetCollection<BsonDocument>(Coll);
    private BsonDocument One(uint id) => Raw().Find(new BsonDocument("_id", (int)id)).Single();
    private long Count(uint id) => Raw().CountDocuments(new BsonDocument("_id", (int)id));

    private static KharaStateDocument Doc(uint id) => new()
    {
        Id = id,
        Points = 1234,
        Active = new List<KharaActiveDocument>
        {
            new() { KharaId = 5, State = 2, Counts = new List<uint> { 0, 7 } },
            new() { KharaId = 9, State = 3, Counts = new List<uint> { 1 } },
        },
        Completed = new List<uint> { 1, 2, 8 },
        UpdatedUtc = Stamp,
    };

    // ------------------------------------------------------------------------------------------------ proof it ran

    [DockerFact]
    public async Task Ran_against_a_real_mongo7_container()
    {
        Assert.True(_fx.Available, "Docker is up but the mongo:7 fixture did not start: " + _fx.StartupError);
        var info = await _fx.Client!.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument("buildInfo", 1));
        var version = info["version"].AsString;
        _out.WriteLine($"MongoKharaStateStoredShapeTests: MongoMailFixture.Available=True mongod={version} at {_fx.ConnectionString}");
        Assert.StartsWith("7.", version);
    }

    // ------------------------------------------------------------------------------------------------ stored shape

    [DockerFact]
    public async Task A_saved_document_has_the_pinned_field_names_and_types_and_reads_back_whole()
    {
        var repo = Repo();
        var id = NewChar();
        await repo.SaveAsync(Doc(id));

        var raw = One(id);
        Assert.Equal(new[] { "_id", "Points", "Active", "Completed", "UpdatedUtc" }, raw.Names.ToArray());
        Assert.Equal(BsonType.Int32, raw["_id"].BsonType);                          // uint character id -> Int32
        Assert.Equal((int)id, raw["_id"].AsInt32);
        Assert.Equal(BsonType.Int32, raw["Points"].BsonType);                       // uint -> Int32
        Assert.Equal(1234, raw["Points"].AsInt32);
        Assert.Equal(BsonType.Array, raw["Active"].BsonType);
        Assert.Equal(2, raw["Active"].AsBsonArray.Count);
        foreach (var e in raw["Active"].AsBsonArray)
        {
            var el = e.AsBsonDocument;
            Assert.Equal(new[] { "KharaId", "State", "Counts" }, el.Names.ToArray());   // element keys, in declaration order
            Assert.Equal(BsonType.Int32, el["KharaId"].BsonType);
            Assert.Equal(BsonType.Int32, el["State"].BsonType);
            Assert.Equal(BsonType.Array, el["Counts"].BsonType);
            Assert.All(el["Counts"].AsBsonArray, c => Assert.Equal(BsonType.Int32, c.BsonType));
        }
        var first = raw["Active"][0].AsBsonDocument;
        Assert.Equal(5, first["KharaId"].AsInt32);
        Assert.Equal(2, first["State"].AsInt32);
        Assert.Equal(new[] { 0, 7 }, first["Counts"].AsBsonArray.Select(c => c.AsInt32).ToArray());
        Assert.Equal(9, raw["Active"][1]["KharaId"].AsInt32);
        Assert.Equal(3, raw["Active"][1]["State"].AsInt32);
        Assert.Equal(BsonType.Array, raw["Completed"].BsonType);
        Assert.All(raw["Completed"].AsBsonArray, c => Assert.Equal(BsonType.Int32, c.BsonType));
        Assert.Equal(new[] { 1, 2, 8 }, raw["Completed"].AsBsonArray.Select(c => c.AsInt32).ToArray());
        Assert.Equal(BsonType.DateTime, raw["UpdatedUtc"].BsonType);
        Assert.Equal(Stamp, raw["UpdatedUtc"].ToUniversalTime());
        Assert.DoesNotContain("\"_t\"", raw.ToJson());                              // no type discriminator anywhere (document or element)

        var got = await repo.GetAsync(id);
        Assert.NotNull(got);
        Assert.Equal(id, got.Id);
        Assert.Equal(1234u, got.Points);
        Assert.Equal(new[] { 5u, 9u }, got.Active.Select(a => a.KharaId).ToArray());
        Assert.Equal(new[] { 2u, 3u }, got.Active.Select(a => a.State).ToArray());
        Assert.Equal(new uint[] { 0, 7 }, got.Active[0].Counts);
        Assert.Equal(new uint[] { 1 }, got.Active[1].Counts);
        Assert.Equal(new uint[] { 1, 2, 8 }, got.Completed);
        Assert.Equal(Stamp, got.UpdatedUtc);
        Assert.Equal(DateTimeKind.Utc, got.UpdatedUtc.Kind);
        Assert.Equal(1, Count(id));                                                 // one document per character
    }

    [DockerFact]
    public async Task Active_and_Completed_come_back_ascending_and_IsCompleted_binary_searches_them()
    {
        var repo = Repo();
        var id = NewChar();
        Raw().InsertOne(new BsonDocument
        {
            { "_id", (int)id }, { "Points", 0 },
            { "Active", new BsonArray
                {
                    new BsonDocument { { "KharaId", 3 }, { "State", 2 }, { "Counts", new BsonArray { 1 } } },
                    new BsonDocument { { "KharaId", 40 }, { "State", 2 }, { "Counts", new BsonArray { 2, 3 } } },
                    new BsonDocument { { "KharaId", 500 }, { "State", 2 }, { "Counts", new BsonArray() } },
                } },
            { "Completed", new BsonArray { 2, 10, 20, 30 } },
            { "UpdatedUtc", new BsonDateTime(Stamp) },
        });

        var got = await repo.GetAsync(id);
        Assert.NotNull(got);
        Assert.Equal(new uint[] { 3, 40, 500 }, got.Active.Select(a => a.KharaId).ToArray());
        Assert.Equal(new uint[] { 2, 10, 20, 30 }, got.Completed);
        Assert.True(got.IsCompleted(2));
        Assert.True(got.IsCompleted(30));
        Assert.False(got.IsCompleted(3));                                           // accepted, not completed
        Assert.False(got.IsCompleted(11));
        Assert.Equal(40u, got.FindActive(40)!.KharaId);
        Assert.Null(got.FindActive(2));
    }

    [DockerFact]
    public async Task The_repository_stores_the_lists_verbatim_and_never_sorts_them()
    {
        var repo = Repo();
        var id = NewChar();
        await repo.SaveAsync(new KharaStateDocument
        {
            Id = id,
            Active = new List<KharaActiveDocument> { new() { KharaId = 9 }, new() { KharaId = 5 } },
            Completed = new List<uint> { 8, 1, 2 },
            UpdatedUtc = Stamp,
        });

        Assert.Equal(new[] { 9, 5 }, One(id)["Active"].AsBsonArray.Select(a => a["KharaId"].AsInt32).ToArray());   // keeping them sorted is the Zone service's job
        Assert.Equal(new[] { 8, 1, 2 }, One(id)["Completed"].AsBsonArray.Select(c => c.AsInt32).ToArray());
    }

    [DockerFact]
    public async Task A_legacy_document_missing_fields_reads_the_documented_defaults_and_unknown_fields_are_ignored()
    {
        var repo = Repo();
        var bare = NewChar();
        Raw().InsertOne(new BsonDocument("_id", (int)bare));

        var got = await repo.GetAsync(bare);
        Assert.NotNull(got);
        Assert.Equal(bare, got.Id);
        Assert.Equal(0u, got.Points);
        Assert.Empty(got.Active);
        Assert.Empty(got.Completed);
        Assert.Equal(default, got.UpdatedUtc);

        // An accepted element without State/Counts reads State = StateInProgress (2) and no counts; a field this build does not know is skipped.
        var id = NewChar();
        Raw().InsertOne(new BsonDocument
        {
            { "_id", (int)id }, { "FromTheFuture", "x" },
            { "Active", new BsonArray { new BsonDocument { { "KharaId", 7 }, { "Extra", 1 } } } },
        });
        var legacy = await repo.GetAsync(id);
        Assert.NotNull(legacy);
        var a = Assert.Single(legacy.Active);
        Assert.Equal(7u, a.KharaId);
        Assert.Equal(KharaActiveDocument.StateInProgress, a.State);
        Assert.Equal(2u, a.State);
        Assert.Empty(a.Counts);
    }

    [DockerFact]
    public async Task Save_upserts_then_replaces_the_whole_document_and_a_read_creates_nothing()
    {
        var repo = Repo();
        var id = NewChar();

        Assert.Null(await repo.GetAsync(id));
        Assert.Equal(0, Count(id));

        await repo.SaveAsync(Doc(id));
        Assert.Equal(1, Count(id));

        await repo.SaveAsync(new KharaStateDocument { Id = id, Points = 5, UpdatedUtc = Stamp.AddDays(1) });
        Assert.Equal(1, Count(id));                                                 // replaced, not a second document
        var raw = One(id);
        Assert.Equal(5, raw["Points"].AsInt32);
        Assert.Empty(raw["Active"].AsBsonArray);                                    // empty lists are stored as empty arrays
        Assert.Empty(raw["Completed"].AsBsonArray);

        var other = NewChar();
        await repo.SaveAsync(Doc(other));
        Assert.Equal(5u, (await repo.GetAsync(id))!.Points);                        // characters are independent
        Assert.Equal(1234u, (await repo.GetAsync(other))!.Points);
    }

    // ------------------------------------------------------------------------------------------------ the uint -> Int32 bound

    [DockerFact]
    public async Task Uint_values_above_int_max_throw_and_int_max_is_the_last_good_value()
    {
        var repo = Repo();

        var id = (uint)int.MaxValue;
        await repo.SaveAsync(new KharaStateDocument
        {
            Id = id, Points = int.MaxValue, Completed = new List<uint> { int.MaxValue }, UpdatedUtc = Stamp,
        });
        var raw = Raw().Find(new BsonDocument("_id", int.MaxValue)).Single();
        Assert.Equal(BsonType.Int32, raw["_id"].BsonType);
        Assert.Equal(int.MaxValue, raw["Points"].AsInt32);
        Assert.Equal(int.MaxValue, Assert.Single(raw["Completed"].AsBsonArray).AsInt32);
        Assert.Equal((uint)int.MaxValue, (await repo.GetAsync(id))!.Points);

        // A value above it sits in the DOCUMENT: BsonSerializationException wrapping the OverflowException, nothing stored.
        var bad = NewChar();
        var ex = await Assert.ThrowsAsync<BsonSerializationException>(() => repo.SaveAsync(new KharaStateDocument { Id = bad, Points = 3_000_000_000u, UpdatedUtc = Stamp }));
        var inner = ex.InnerException;
        while (inner is BsonSerializationException) inner = inner.InnerException;
        Assert.IsType<OverflowException>(inner);
        Assert.Equal(0, Count(bad));

        // A character id above it sits in the FILTER: the raw OverflowException, on a read.
        await Assert.ThrowsAsync<OverflowException>(() => repo.GetAsync(3_000_000_000u));
    }
}

/// <summary>The <c>character_khara_title</c> collection: <see cref="KharaTitleDocument"/> and the no-upsert play-clock update.</summary>
[Collection("MongoMail")]
public sealed class MongoKharaTitleStoredShapeTests
{
    private const string Db = "ro2_it_khara_title";
    private const string Coll = "character_khara_title";
    private static long _charSeq = Random.Shared.Next(1_000_000, 1_000_000_000);

    private readonly MongoMailFixture _fx;
    private readonly ITestOutputHelper _out;
    public MongoKharaTitleStoredShapeTests(MongoMailFixture fx, ITestOutputHelper output) { _fx = fx; _out = output; }

    private static uint NewChar() => (uint)Interlocked.Increment(ref _charSeq);

    private static DateTime Utc(int year, int month, int day, int hour, int minute, int second)
        => new(year, month, day, hour, minute, second, DateTimeKind.Utc);

    private KharaTitleRepository Repo()
    {
        Assert.True(_fx.Available, "Docker is up but the mongo:7 fixture did not start: " + _fx.StartupError);
        return new KharaTitleRepository(new MongoDbContext(_fx.ConnectionString, Db));
    }

    private IMongoCollection<BsonDocument> Raw() => _fx.Client!.GetDatabase(Db).GetCollection<BsonDocument>(Coll);
    private BsonDocument One(uint id) => Raw().Find(new BsonDocument("_id", (int)id)).Single();
    private long Count(uint id) => Raw().CountDocuments(new BsonDocument("_id", (int)id));

    // ------------------------------------------------------------------------------------------------ proof it ran

    [DockerFact]
    public async Task Ran_against_a_real_mongo7_container()
    {
        Assert.True(_fx.Available, "Docker is up but the mongo:7 fixture did not start: " + _fx.StartupError);
        var info = await _fx.Client!.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument("buildInfo", 1));
        var version = info["version"].AsString;
        _out.WriteLine($"MongoKharaTitleStoredShapeTests: MongoMailFixture.Available=True mongod={version} at {_fx.ConnectionString}");
        Assert.StartsWith("7.", version);
    }

    // ------------------------------------------------------------------------------------------------ stored shape

    [DockerFact]
    public async Task A_saved_document_has_the_pinned_field_names_and_types_and_reads_back_whole()
    {
        var repo = Repo();
        var id = NewChar();
        var doc = new KharaTitleDocument
        {
            Id = id, KharaA = 4242, KharaB = 77,
            ATimerStartUtc = Utc(2026, 3, 4, 5, 6, 7),
            LastSuccessUtc = Utc(2026, 3, 4, 6, 7, 8),
            PlayClockUtc = Utc(2026, 3, 4, 7, 8, 9),
            UpdatedUtc = Utc(2026, 3, 4, 8, 9, 10),
        };
        await repo.SaveAsync(doc);

        var raw = One(id);
        Assert.Equal(new[] { "_id", "KharaA", "KharaB", "ATimerStartUtc", "LastSuccessUtc", "PlayClockUtc", "UpdatedUtc" }, raw.Names.ToArray());
        Assert.Equal(BsonType.Int32, raw["_id"].BsonType);                          // uint character id -> Int32
        Assert.Equal((int)id, raw["_id"].AsInt32);
        Assert.Equal(BsonType.Int32, raw["KharaA"].BsonType);
        Assert.Equal(4242, raw["KharaA"].AsInt32);
        Assert.Equal(BsonType.Int32, raw["KharaB"].BsonType);
        Assert.Equal(77, raw["KharaB"].AsInt32);
        foreach (var name in new[] { "ATimerStartUtc", "LastSuccessUtc", "PlayClockUtc", "UpdatedUtc" })
            Assert.Equal(BsonType.DateTime, raw[name].BsonType);
        Assert.Equal(doc.ATimerStartUtc, raw["ATimerStartUtc"].ToUniversalTime());
        Assert.Equal(doc.LastSuccessUtc, raw["LastSuccessUtc"].ToUniversalTime());
        Assert.Equal(doc.PlayClockUtc, raw["PlayClockUtc"].ToUniversalTime());
        Assert.Equal(doc.UpdatedUtc, raw["UpdatedUtc"].ToUniversalTime());
        Assert.DoesNotContain("\"_t\"", raw.ToJson());

        var got = await repo.GetAsync(id);
        Assert.NotNull(got);
        Assert.Equal(id, got.Id);
        Assert.Equal(4242u, got.KharaA);
        Assert.Equal(77u, got.KharaB);
        Assert.Equal(doc.ATimerStartUtc, got.ATimerStartUtc);
        Assert.Equal(doc.LastSuccessUtc, got.LastSuccessUtc);
        Assert.Equal(doc.PlayClockUtc, got.PlayClockUtc);
        Assert.Equal(doc.UpdatedUtc, got.UpdatedUtc);
        Assert.Equal(DateTimeKind.Utc, got.UpdatedUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, got.PlayClockUtc!.Value.Kind);
        Assert.Equal(1, Count(id));
    }

    [DockerFact]
    public async Task Null_instants_are_stored_as_BsonNull_and_read_back_null()
    {
        var repo = Repo();
        var id = NewChar();
        await repo.SaveAsync(new KharaTitleDocument { Id = id, UpdatedUtc = Utc(2026, 1, 2, 3, 4, 5) });

        var raw = One(id);
        Assert.Equal(new[] { "_id", "KharaA", "KharaB", "ATimerStartUtc", "LastSuccessUtc", "PlayClockUtc", "UpdatedUtc" }, raw.Names.ToArray());   // null fields are written, not omitted
        Assert.Equal(0, raw["KharaA"].AsInt32);                                     // 0 = no title worn
        Assert.Equal(0, raw["KharaB"].AsInt32);
        Assert.Equal(BsonType.Null, raw["ATimerStartUtc"].BsonType);
        Assert.Equal(BsonType.Null, raw["LastSuccessUtc"].BsonType);
        Assert.Equal(BsonType.Null, raw["PlayClockUtc"].BsonType);

        var got = await repo.GetAsync(id);
        Assert.NotNull(got);
        Assert.Null(got.ATimerStartUtc);
        Assert.Null(got.LastSuccessUtc);
        Assert.Null(got.PlayClockUtc);
    }

    [DockerFact]
    public async Task A_legacy_document_without_PlayClockUtc_reads_null_and_the_play_clock_falls_back_to_the_wall_clock()
    {
        var repo = Repo();
        var id = NewChar();
        var legacy = Utc(2026, 2, 3, 4, 5, 6);
        // A document written before the play clock: the PlayClockUtc field is absent (not null).
        Raw().InsertOne(new BsonDocument
        {
            { "_id", (int)id }, { "KharaA", 12 }, { "KharaB", 0 },
            { "ATimerStartUtc", new BsonDateTime(legacy) }, { "LastSuccessUtc", new BsonDateTime(legacy) },
            { "UpdatedUtc", new BsonDateTime(legacy) },
        });

        var got = await repo.GetAsync(id);
        Assert.NotNull(got);
        Assert.Null(got.PlayClockUtc);                                              // the repository adds no default: the fallback lives in the Zone's KharaTitleClock
        Assert.Equal(legacy, got.ATimerStartUtc);                                   // its instants are wall-clock ones

        // KharaTitleStore.PreloadAsync does exactly this: Seed(stored PlayClockUtc, wall now). A null stored instant seeds the clock at the wall clock.
        var wall = Utc(2026, 11, 2, 12, 0, 0);
        try
        {
            KharaTitleClock.Seed(id, got.PlayClockUtc, wall);
            Assert.Equal(wall, KharaTitleClock.PlayNow(id, wall.AddHours(3)));      // seeded stopped at the wall-clock instant
        }
        finally { KharaTitleClock.Forget(id); }

        // A stored PlayClockUtc seeds the clock at that instant instead.
        var withClock = NewChar();
        await repo.SaveAsync(new KharaTitleDocument { Id = withClock, PlayClockUtc = legacy, UpdatedUtc = legacy });
        var stored = await repo.GetAsync(withClock);
        try
        {
            KharaTitleClock.Seed(withClock, stored!.PlayClockUtc, wall);
            Assert.Equal(legacy, KharaTitleClock.PlayNow(withClock, wall.AddHours(3)));
        }
        finally { KharaTitleClock.Forget(withClock); }
    }

    [DockerFact]
    public async Task Instants_are_truncated_to_milliseconds_and_local_ones_are_normalised_to_utc()
    {
        var repo = Repo();
        var id = NewChar();
        var withTicks = Utc(2026, 7, 8, 9, 10, 11).AddTicks(1_234_567);             // 123.4567 ms
        var local = Utc(2026, 7, 8, 12, 0, 0).ToLocalTime();
        await repo.SaveAsync(new KharaTitleDocument { Id = id, UpdatedUtc = withTicks, PlayClockUtc = local, ATimerStartUtc = withTicks, LastSuccessUtc = withTicks });

        var raw = One(id);
        var truncated = Utc(2026, 7, 8, 9, 10, 11).AddMilliseconds(123);
        Assert.Equal(truncated, raw["UpdatedUtc"].ToUniversalTime());
        Assert.Equal(truncated, raw["ATimerStartUtc"].ToUniversalTime());
        Assert.Equal(truncated, raw["LastSuccessUtc"].ToUniversalTime());
        Assert.Equal(Utc(2026, 7, 8, 12, 0, 0), raw["PlayClockUtc"].ToUniversalTime());

        var got = await repo.GetAsync(id);
        Assert.NotNull(got);
        Assert.Equal(truncated, got.UpdatedUtc);
        Assert.Equal(Utc(2026, 7, 8, 12, 0, 0), got.PlayClockUtc);
        Assert.Equal(DateTimeKind.Utc, got.PlayClockUtc!.Value.Kind);
    }

    [DockerFact]
    public async Task Save_upserts_then_replaces_the_whole_document_including_back_to_null()
    {
        var repo = Repo();
        var id = NewChar();
        Assert.Null(await repo.GetAsync(id));
        Assert.Equal(0, Count(id));

        await repo.SaveAsync(new KharaTitleDocument
        {
            Id = id, KharaA = 10, KharaB = 20, ATimerStartUtc = Utc(2026, 5, 1, 0, 0, 0),
            LastSuccessUtc = Utc(2026, 5, 1, 0, 0, 1), PlayClockUtc = Utc(2026, 5, 1, 0, 0, 2), UpdatedUtc = Utc(2026, 5, 1, 0, 0, 3),
        });
        await repo.SaveAsync(new KharaTitleDocument
        {
            Id = id, KharaA = 11, KharaB = 0, ATimerStartUtc = null,
            LastSuccessUtc = Utc(2026, 6, 1, 0, 0, 1), PlayClockUtc = null, UpdatedUtc = Utc(2026, 6, 1, 0, 0, 3),
        });

        Assert.Equal(1, Count(id));
        var got = await repo.GetAsync(id);
        Assert.NotNull(got);
        Assert.Equal(11u, got.KharaA);
        Assert.Equal(0u, got.KharaB);
        Assert.Null(got.ATimerStartUtc);
        Assert.Equal(Utc(2026, 6, 1, 0, 0, 1), got.LastSuccessUtc);
        Assert.Null(got.PlayClockUtc);
        Assert.Equal(Utc(2026, 6, 1, 0, 0, 3), got.UpdatedUtc);
    }

    // ------------------------------------------------------------------------------------------------ the play-clock update

    [DockerFact]
    public async Task StorePlayClock_sets_only_PlayClockUtc_and_UpdatedUtc_on_an_existing_document()
    {
        var repo = Repo();
        var id = NewChar();
        await repo.SaveAsync(new KharaTitleDocument
        {
            Id = id, KharaA = 5, KharaB = 6, ATimerStartUtc = Utc(2026, 4, 1, 1, 1, 1),
            LastSuccessUtc = Utc(2026, 4, 1, 2, 2, 2), PlayClockUtc = Utc(2026, 4, 1, 3, 3, 3), UpdatedUtc = Utc(2026, 4, 1, 4, 4, 4),
        });

        var newClock = Utc(2026, 4, 2, 10, 0, 0).AddTicks(1_234_567);
        var newUpdated = Utc(2026, 4, 2, 11, 0, 0);
        await repo.StorePlayClockAsync(id, newClock, newUpdated);

        var raw = One(id);
        Assert.Equal(new[] { "_id", "KharaA", "KharaB", "ATimerStartUtc", "LastSuccessUtc", "PlayClockUtc", "UpdatedUtc" }, raw.Names.ToArray());   // an in-place $set: same fields, same order
        Assert.Equal(5, raw["KharaA"].AsInt32);
        Assert.Equal(6, raw["KharaB"].AsInt32);
        Assert.Equal(Utc(2026, 4, 1, 1, 1, 1), raw["ATimerStartUtc"].ToUniversalTime());
        Assert.Equal(Utc(2026, 4, 1, 2, 2, 2), raw["LastSuccessUtc"].ToUniversalTime());
        Assert.Equal(Utc(2026, 4, 2, 10, 0, 0).AddMilliseconds(123), raw["PlayClockUtc"].ToUniversalTime());   // ms-truncated
        Assert.Equal(newUpdated, raw["UpdatedUtc"].ToUniversalTime());
        Assert.Equal(1, Count(id));
    }

    [DockerFact]
    public async Task StorePlayClock_sets_the_clock_on_a_document_that_had_none_and_on_a_legacy_one_without_the_field()
    {
        var repo = Repo();
        var id = NewChar();
        await repo.SaveAsync(new KharaTitleDocument { Id = id, KharaA = 9, UpdatedUtc = Utc(2026, 2, 2, 2, 2, 2) });
        await repo.StorePlayClockAsync(id, Utc(2026, 2, 3, 0, 0, 0), Utc(2026, 2, 3, 0, 0, 1));
        var got = await repo.GetAsync(id);
        Assert.Equal(9u, got!.KharaA);
        Assert.Equal(Utc(2026, 2, 3, 0, 0, 0), got.PlayClockUtc);
        Assert.Equal(Utc(2026, 2, 3, 0, 0, 1), got.UpdatedUtc);

        var legacy = NewChar();
        Raw().InsertOne(new BsonDocument { { "_id", (int)legacy }, { "KharaA", 3 }, { "KharaB", 0 }, { "UpdatedUtc", new BsonDateTime(Utc(2026, 1, 1, 0, 0, 0)) } });
        await repo.StorePlayClockAsync(legacy, Utc(2026, 2, 3, 0, 0, 0), Utc(2026, 2, 3, 0, 0, 1));
        Assert.Equal(Utc(2026, 2, 3, 0, 0, 0), One(legacy)["PlayClockUtc"].ToUniversalTime());
        Assert.Equal(1, Count(legacy));
    }

    [DockerFact]
    public async Task StorePlayClock_for_a_missing_character_creates_nothing()
    {
        var repo = Repo();
        var id = NewChar();

        await repo.StorePlayClockAsync(id, Utc(2026, 8, 1, 0, 0, 0), Utc(2026, 8, 1, 0, 0, 1));   // UpdateOne with IsUpsert = false

        Assert.Equal(0, Count(id));
        Assert.Null(await repo.GetAsync(id));
    }

    [DockerFact]
    public async Task StorePlayClock_touches_only_the_named_character()
    {
        var repo = Repo();
        var a = NewChar();
        var b = NewChar();
        await repo.SaveAsync(new KharaTitleDocument { Id = a, KharaA = 1, UpdatedUtc = Utc(2026, 9, 1, 0, 0, 0) });
        await repo.SaveAsync(new KharaTitleDocument { Id = b, KharaA = 2, UpdatedUtc = Utc(2026, 9, 1, 0, 0, 0) });

        await repo.StorePlayClockAsync(a, Utc(2026, 9, 2, 0, 0, 0), Utc(2026, 9, 2, 0, 0, 1));

        Assert.Equal(Utc(2026, 9, 2, 0, 0, 0), (await repo.GetAsync(a))!.PlayClockUtc);
        Assert.Null((await repo.GetAsync(b))!.PlayClockUtc);
        Assert.Equal(Utc(2026, 9, 1, 0, 0, 0), (await repo.GetAsync(b))!.UpdatedUtc);
    }

    // ------------------------------------------------------------------------------------------------ the uint -> Int32 bound

    [DockerFact]
    public async Task Uint_values_above_int_max_throw_and_int_max_is_the_last_good_value()
    {
        var repo = Repo();

        var id = (uint)int.MaxValue;
        await repo.SaveAsync(new KharaTitleDocument { Id = id, KharaA = int.MaxValue, KharaB = int.MaxValue, UpdatedUtc = Utc(2026, 1, 1, 0, 0, 0) });
        var raw = Raw().Find(new BsonDocument("_id", int.MaxValue)).Single();
        Assert.Equal(BsonType.Int32, raw["_id"].BsonType);
        Assert.Equal(int.MaxValue, raw["KharaA"].AsInt32);
        Assert.Equal((uint)int.MaxValue, (await repo.GetAsync(id))!.KharaB);

        var bad = NewChar();
        var ex = await Assert.ThrowsAsync<BsonSerializationException>(() => repo.SaveAsync(new KharaTitleDocument { Id = bad, KharaA = 3_000_000_000u, UpdatedUtc = Utc(2026, 1, 1, 0, 0, 0) }));
        var inner = ex.InnerException;
        while (inner is BsonSerializationException) inner = inner.InnerException;
        Assert.IsType<OverflowException>(inner);
        Assert.Equal(0, Count(bad));

        await Assert.ThrowsAsync<OverflowException>(() => repo.GetAsync(3_000_000_000u));
        await Assert.ThrowsAsync<OverflowException>(() => repo.StorePlayClockAsync(3_000_000_000u, Utc(2026, 1, 1, 0, 0, 0), Utc(2026, 1, 1, 0, 0, 0)));
    }
}

/// <summary>The <c>khara_traces</c> collection: the repository-private trace document, list stored verbatim.</summary>
[Collection("MongoMail")]
public sealed class MongoKharaTraceStoredShapeTests
{
    private const string Db = "ro2_it_khara_trace";
    private const string Coll = "khara_traces";
    private static long _charSeq = Random.Shared.Next(1_000_000, 1_000_000_000);

    private readonly MongoMailFixture _fx;
    private readonly ITestOutputHelper _out;
    public MongoKharaTraceStoredShapeTests(MongoMailFixture fx, ITestOutputHelper output) { _fx = fx; _out = output; }

    private static uint NewChar() => (uint)Interlocked.Increment(ref _charSeq);

    private KharaTraceRepository Repo()
    {
        Assert.True(_fx.Available, "Docker is up but the mongo:7 fixture did not start: " + _fx.StartupError);
        return new KharaTraceRepository(new MongoDbContext(_fx.ConnectionString, Db));
    }

    private IMongoCollection<BsonDocument> Raw() => _fx.Client!.GetDatabase(Db).GetCollection<BsonDocument>(Coll);
    private BsonDocument One(uint id) => Raw().Find(new BsonDocument("_id", (int)id)).Single();
    private long Count(uint id) => Raw().CountDocuments(new BsonDocument("_id", (int)id));

    // ------------------------------------------------------------------------------------------------ proof it ran

    [DockerFact]
    public async Task Ran_against_a_real_mongo7_container()
    {
        Assert.True(_fx.Available, "Docker is up but the mongo:7 fixture did not start: " + _fx.StartupError);
        var info = await _fx.Client!.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument("buildInfo", 1));
        var version = info["version"].AsString;
        _out.WriteLine($"MongoKharaTraceStoredShapeTests: MongoMailFixture.Available=True mongod={version} at {_fx.ConnectionString}");
        Assert.StartsWith("7.", version);
    }

    // ------------------------------------------------------------------------------------------------ stored shape

    [DockerFact]
    public async Task A_saved_list_has_the_pinned_field_names_and_types_and_keeps_order_and_duplicates()
    {
        var repo = Repo();
        var id = NewChar();
        var ids = new uint[] { 5, 5, 3, 9, 3 };
        await repo.SaveAsync(id, ids);

        var raw = One(id);
        Assert.Equal(new[] { "_id", "KharaIds" }, raw.Names.ToArray());
        Assert.Equal(BsonType.Int32, raw["_id"].BsonType);                          // uint character id -> Int32
        Assert.Equal((int)id, raw["_id"].AsInt32);
        Assert.Equal(BsonType.Array, raw["KharaIds"].BsonType);
        Assert.All(raw["KharaIds"].AsBsonArray, v => Assert.Equal(BsonType.Int32, v.BsonType));   // uint -> Int32
        Assert.Equal(new[] { 5, 5, 3, 9, 3 }, raw["KharaIds"].AsBsonArray.Select(v => v.AsInt32).ToArray());   // verbatim: not sorted, not de-duplicated
        Assert.DoesNotContain("\"_t\"", raw.ToJson());

        Assert.Equal(ids, await repo.GetAsync(id));
        Assert.Equal(1, Count(id));
    }

    [DockerFact]
    public async Task A_seeded_raw_list_reads_back_in_the_same_order_with_its_duplicates()
    {
        var repo = Repo();
        var id = NewChar();
        Raw().InsertOne(new BsonDocument { { "_id", (int)id }, { "KharaIds", new BsonArray { 5, 5, 3, 9, 3 } } });

        Assert.Equal(new uint[] { 5, 5, 3, 9, 3 }, await repo.GetAsync(id));
    }

    [DockerFact]
    public async Task Save_replaces_the_whole_list_an_empty_list_is_an_empty_array_and_unknown_is_empty_without_a_write()
    {
        var repo = Repo();
        var id = NewChar();

        Assert.Empty(await repo.GetAsync(id));
        Assert.Equal(0, Count(id));                                                 // a read upserts nothing

        await repo.SaveAsync(id, new uint[] { 1, 2, 3 });
        await repo.SaveAsync(id, new uint[] { 9, 1 });                              // 1434 always carries the full set, never a delta
        Assert.Equal(new uint[] { 9, 1 }, await repo.GetAsync(id));
        Assert.Equal(1, Count(id));

        await repo.SaveAsync(id, Array.Empty<uint>());
        Assert.Empty(One(id)["KharaIds"].AsBsonArray);                              // the document stays, with an empty array
        Assert.Empty(await repo.GetAsync(id));

        var other = NewChar();
        await repo.SaveAsync(other, new uint[] { 4 });
        Assert.Empty(await repo.GetAsync(id));                                      // characters are independent
        Assert.Equal(new uint[] { 4 }, await repo.GetAsync(other));
    }

    [DockerFact]
    public async Task A_document_without_KharaIds_reads_as_an_empty_list()
    {
        var repo = Repo();
        var id = NewChar();
        Raw().InsertOne(new BsonDocument("_id", (int)id));

        Assert.Empty(await repo.GetAsync(id));
    }

    // ------------------------------------------------------------------------------------------------ the uint -> Int32 bound

    [DockerFact]
    public async Task Uint_values_above_int_max_throw_and_int_max_is_the_last_good_value()
    {
        var repo = Repo();

        var id = (uint)int.MaxValue;
        await repo.SaveAsync(id, new uint[] { int.MaxValue });
        var raw = Raw().Find(new BsonDocument("_id", int.MaxValue)).Single();
        Assert.Equal(BsonType.Int32, raw["_id"].BsonType);
        Assert.Equal(int.MaxValue, Assert.Single(raw["KharaIds"].AsBsonArray).AsInt32);
        Assert.Equal(new uint[] { int.MaxValue }, await repo.GetAsync(id));

        var bad = NewChar();
        var ex = await Assert.ThrowsAsync<BsonSerializationException>(() => repo.SaveAsync(bad, new uint[] { 3_000_000_000u }));
        var inner = ex.InnerException;
        while (inner is BsonSerializationException) inner = inner.InnerException;
        Assert.IsType<OverflowException>(inner);
        Assert.Equal(0, Count(bad));

        await Assert.ThrowsAsync<OverflowException>(() => repo.GetAsync(3_000_000_000u));
    }
}
