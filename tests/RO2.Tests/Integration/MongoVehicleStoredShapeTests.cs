using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using RO2.Companions.Domain;
using RO2.Persistence.Mongo.Persistence;
using RO2.Persistence.Mongo.Repositories;
using Xunit;
using Xunit.Abstractions;

namespace RO2.Tests.Integration;

/// <summary>
/// B11 S1: the Mongo twin of <see cref="PostgresVehicleTests"/>, against a REAL mongo:7 container (the <see cref="MongoMailFixture"/>
/// of the Mail twin, its own database). It pins the stored shape of the owned-vehicle document (the repository-private
/// <c>VehicleDocument</c> in <c>character_vehicles</c> holding <see cref="OwnedVehicle"/> elements) and the optimistic CAS of the
/// Mongo <c>VehicleRepository</c>, so a byte-identical move of the entity, the rules or the repository keeps it green. Every value
/// below was observed from the current code, not derived. Every test reports SKIPPED without Docker and asserts the fixture started
/// when Docker is up.
/// </summary>
[Collection("MongoMail")]
public sealed class MongoVehicleStoredShapeTests
{
    private const string Db = "ro2_it_vehicles";
    private const string Coll = "character_vehicles";
    private const double Now = 46223.5720;
    private static long _charSeq = Random.Shared.Next(1_000_000, 1_000_000_000);

    private readonly MongoMailFixture _fx;
    private readonly ITestOutputHelper _out;
    public MongoVehicleStoredShapeTests(MongoMailFixture fx, ITestOutputHelper output) { _fx = fx; _out = output; }

    // Mongo's UInt32Serializer writes Int32 and throws above int.MaxValue (see Uints_above_int_max_throw...), so every uint id used
    // as a stored value stays within int.MaxValue (the Postgres twin's 2.2e9..4.2e9 ids do NOT work here).
    private static uint NewChar() => (uint)Interlocked.Increment(ref _charSeq);

    private VehicleRepository Repo(string db = Db)
    {
        Assert.True(_fx.Available, "Docker is up but the mongo:7 fixture did not start: " + _fx.StartupError);
        return new VehicleRepository(new MongoDbContext(_fx.ConnectionString, db));
    }

    private IMongoCollection<BsonDocument> Raw(string db = Db)
        => _fx.Client!.GetDatabase(db).GetCollection<BsonDocument>(Coll);

    private BsonDocument One(uint id, string db = Db)
        => Raw(db).Find(new BsonDocument("_id", (int)id)).Single();

    private long Count(uint id, string db = Db) => Raw(db).CountDocuments(new BsonDocument("_id", (int)id));

    private static BsonDocument Element(int ridingId, double expiry)
        => new() { { "RidingId", ridingId }, { "ExpiryOle", expiry } };

    /// <summary>Inserts a raw document; <paramref name="updatedUtc"/> null omits the field (a legacy document).</summary>
    private void Seed(uint id, BsonArray vehicles, DateTime? updatedUtc, string db = Db)
    {
        var doc = new BsonDocument { { "_id", (int)id }, { "Vehicles", vehicles } };
        if (updatedUtc is { } t) doc["UpdatedUtc"] = new BsonDateTime(t);
        Raw(db).InsertOne(doc, new InsertOneOptions { BypassDocumentValidation = true });
    }

    private static string Bits(double d) => Convert.ToHexStringLower(BitConverter.GetBytes(d));

    // ------------------------------------------------------------------------------------------------ proof it ran

    [DockerFact]
    public async Task Ran_against_a_real_mongo7_container()
    {
        Assert.True(_fx.Available, "Docker is up but the mongo:7 fixture did not start: " + _fx.StartupError);
        var info = await _fx.Client!.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument("buildInfo", 1));
        var version = info["version"].AsString;
        _out.WriteLine($"MongoVehicleStoredShapeTests: MongoMailFixture.Available=True mongod={version} at {_fx.ConnectionString}");
        Assert.StartsWith("7.", version);
    }

    // ------------------------------------------------------------------------------------------------ stored shape

    [DockerFact]
    public async Task The_document_shape_is_pinned_and_a_grant_stores_float32_expiries_sorted_by_riding_id()
    {
        var repo = Repo();
        var id = NewChar();

        await repo.GrantAsync(id, 90280000, 14, Now);       // timed
        await repo.GrantAsync(id, 90150000, null, Now);     // permanent
        var before = DateTime.UtcNow;
        var result = await repo.GrantAsync(id, 90190000, 5, Now);
        var after = DateTime.UtcNow;

        var raw = One(id);
        Assert.Equal(new[] { "_id", "Vehicles", "UpdatedUtc" }, raw.Names.ToArray());   // Id -> _id, declaration order
        Assert.Equal(BsonType.Int32, raw["_id"].BsonType);                              // uint character id -> Int32
        Assert.Equal((int)id, raw["_id"].AsInt32);
        Assert.Equal(BsonType.Array, raw["Vehicles"].BsonType);

        var vehicles = raw["Vehicles"].AsBsonArray;
        Assert.Equal(new[] { 90150000, 90190000, 90280000 }, vehicles.Select(v => v["RidingId"].AsInt32).ToArray());   // sorted by Grant
        foreach (var v in vehicles)
        {
            Assert.Equal(new[] { "RidingId", "ExpiryOle" }, v.AsBsonDocument.Names.ToArray());   // element field order
            Assert.Equal(BsonType.Int32, v["RidingId"].BsonType);                                // uint -> Int32
            Assert.Equal(BsonType.Double, v["ExpiryOle"].BsonType);
        }
        Assert.Equal(0d, vehicles[0]["ExpiryOle"].AsDouble);                                      // permanent stays a Double 0
        Assert.Equal(Bits((double)(float)(Now + 5)), Bits(vehicles[1]["ExpiryOle"].AsDouble));    // float32-rounded, stored bit for bit
        Assert.Equal(Bits((double)(float)(Now + 14)), Bits(vehicles[2]["ExpiryOle"].AsDouble));
        Assert.NotEqual(Now + 5, vehicles[1]["ExpiryOle"].AsDouble);                              // the float32 rounding is real

        Assert.Equal(BsonType.DateTime, raw["UpdatedUtc"].BsonType);                              // stored as a Date, UTC, ms resolution
        var stamp = raw["UpdatedUtc"].ToUniversalTime();
        Assert.InRange(stamp, new DateTime(before.Ticks - before.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc), after);

        // The repository read gives back the same list, in the stored order.
        Assert.Equal(result.Select(v => v.RidingId), (await repo.GetAsync(id)).Select(v => v.RidingId));
        Assert.Equal(new[] { 90150000u, 90190000u, 90280000u }, result.Select(v => v.RidingId));
        Assert.Single(await Raw().Find(new BsonDocument("_id", (int)id)).ToListAsync());          // one document per character
    }

    [DockerFact]
    public async Task Get_returns_the_stored_order_unsorted_and_an_unknown_character_is_empty_and_stores_nothing()
    {
        var repo = Repo();
        var id = NewChar();
        Seed(id, new BsonArray { Element(90280000, 0), Element(90150000, 46000.5) }, new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc));

        var stored = await repo.GetAsync(id);

        Assert.Equal(new[] { 90280000u, 90150000u }, stored.Select(v => v.RidingId));
        Assert.Equal(46000.5, stored[1].ExpiryOle);

        var absent = NewChar();
        Assert.Empty(await repo.GetAsync(absent));
        Assert.Empty(await repo.LoadAndCutToWholeSecondsAsync(absent));
        Assert.Equal(0, Count(absent));                                                           // a read (and a cut of nothing) upserts nothing
    }

    [DockerFact]
    public async Task A_legacy_document_without_UpdatedUtc_loads_but_no_write_can_ever_land_on_it()
    {
        var repo = Repo();
        var id = NewChar();
        Seed(id, new BsonArray { Element(90190000, 0), Element(90280000, 46223.57421875) }, updatedUtc: null);

        // Loads fine: the missing UpdatedUtc is just the default (MinValue).
        var stored = await repo.GetAsync(id);
        Assert.Equal(new[] { 90190000u, 90280000u }, stored.Select(v => v.RidingId));
        Assert.Equal(46223.57421875, stored[1].ExpiryOle);

        // The CAS filter is "UpdatedUtc == default(DateTime)", which a MISSING field never matches: every attempt reports "someone else
        // got in first", so a grant and an extension burn all 5 attempts and throw, and the document is left exactly as it was.
        var grant = await Assert.ThrowsAsync<InvalidOperationException>(() => repo.GrantAsync(id, 90150000, null, Now));
        Assert.Equal($"The vehicle list of character {id} kept changing; gave up after 5 attempts.", grant.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.TryExtendTimedAsync(id, 90280000, 5, Now));

        // The load-time cut swallows that failure and returns the cut list UNSTORED.
        var cut = await repo.LoadAndCutToWholeSecondsAsync(id);
        Assert.Equal(new[] { 90190000u, 90280000u }, cut.Select(v => v.RidingId));
        Assert.Equal(VehicleRules.WholeSecondExpiry(46223.57421875), cut[1].ExpiryOle);
        Assert.NotEqual(46223.57421875, cut[1].ExpiryOle);

        var raw = One(id);
        Assert.Equal(new[] { "_id", "Vehicles" }, raw.Names.ToArray());                           // still no UpdatedUtc
        Assert.Equal(46223.57421875, raw["Vehicles"][1]["ExpiryOle"].AsDouble);                   // and the stored expiry is uncut
        Assert.Equal(2, raw["Vehicles"].AsBsonArray.Count);
    }

    // ------------------------------------------------------------------------------------------------ the CAS

    [DockerFact]
    public async Task A_write_stamps_UpdatedUtc_to_the_ms_and_always_moves_it_strictly_forward()
    {
        var repo = Repo();
        var id = NewChar();

        // A stamp in the FUTURE can never be "newer" than the clock: the next write is bumped to read + 1 ms exactly.
        var future = new DateTime(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Seed(id, new BsonArray { Element(90190000, 0) }, future);
        await repo.GrantAsync(id, 90280000, null, Now);
        Assert.Equal(future.AddMilliseconds(1), One(id)["UpdatedUtc"].ToUniversalTime());
        await repo.GrantAsync(id, 90150000, null, Now);
        Assert.Equal(future.AddMilliseconds(2), One(id)["UpdatedUtc"].ToUniversalTime());         // bumped again, +1 ms per write

        // Back to back writes on a normal document: the stamp strictly increases even inside one millisecond.
        var normal = NewChar();
        await repo.GrantAsync(normal, 90190000, null, Now);
        var last = One(normal)["UpdatedUtc"].ToUniversalTime();
        for (var i = 0; i < 5; i++)
        {
            await repo.GrantAsync(normal, 90190000, null, Now);
            var next = One(normal)["UpdatedUtc"].ToUniversalTime();
            Assert.True(next > last, $"stamp {next:O} is not after {last:O}");
            last = next;
        }

        // A stamp in the past is replaced by the clock (not read + 1 ms).
        var old = NewChar();
        var past = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
        Seed(old, new BsonArray { Element(90190000, 0) }, past);
        var t0 = DateTime.UtcNow;
        await repo.GrantAsync(old, 90280000, null, Now);
        Assert.True(One(old)["UpdatedUtc"].ToUniversalTime() >= new DateTime(t0.Ticks - t0.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc));
    }

    [DockerFact]
    public async Task Concurrent_first_grants_for_one_character_all_land_through_the_insert_race_and_the_retry()
    {
        var repo = Repo();
        var id = NewChar();

        // Four writers read "no document" together: one inserts, the others hit DuplicateKey (a lost race, not an error) and retry from a
        // fresh read. Each round at least one writer wins, so 4 writers always fit in MaxAttempts = 5.
        var ids = new uint[] { 90190000, 90220000, 90280000, 90150000 };
        await Task.WhenAll(ids.Select(r => Task.Run(() => repo.GrantAsync(id, r, null, Now))));

        Assert.Equal(ids.OrderBy(x => x), (await repo.GetAsync(id)).Select(v => v.RidingId));
        Assert.Equal(1, Count(id));
    }

    [DockerFact]
    public async Task A_concurrent_cut_never_loses_a_grant()
    {
        var repo = Repo();
        var id = NewChar();
        await repo.GrantAsync(id, 90190000, 5, 46223.5724);                                       // a fractional-second expiry the cut will rewrite

        await Task.WhenAll(
            Task.Run(() => repo.LoadAndCutToWholeSecondsAsync(id)),
            Task.Run(() => repo.GrantAsync(id, 90280000, null, Now)));

        Assert.Equal(new[] { 90190000u, 90280000u }, (await repo.GetAsync(id)).Select(v => v.RidingId));
    }

    // ------------------------------------------------------------------------------------------------ the rules through the store

    [DockerFact]
    public async Task Extend_is_null_and_stores_nothing_unless_the_vehicle_is_owned_and_timed()
    {
        var repo = Repo();
        var id = NewChar();

        Assert.Null(await repo.TryExtendTimedAsync(id, 90190000, 5, Now));
        Assert.Equal(0, Count(id));                                                               // not even a document

        await repo.GrantAsync(id, 90280000, 14, Now);
        await repo.GrantAsync(id, 90150000, null, Now);
        var stampBefore = One(id)["UpdatedUtc"].ToUniversalTime();
        Assert.Null(await repo.TryExtendTimedAsync(id, 90190000, 5, Now));                        // not owned
        Assert.Null(await repo.TryExtendTimedAsync(id, 90150000, 5, Now));                        // permanent
        Assert.Equal(stampBefore, One(id)["UpdatedUtc"].ToUniversalTime());                       // no write, not even a stamp bump

        var granted = await repo.GrantAsync(id, 90190000, 5, Now);
        var extended = await repo.TryExtendTimedAsync(id, 90190000, 5, Now);
        Assert.Equal(new[] { 90150000u, 90190000u, 90280000u }, extended!.Select(v => v.RidingId));
        Assert.Equal((double)(float)(granted.Single(v => v.RidingId == 90190000).ExpiryOle + 5), extended!.Single(v => v.RidingId == 90190000).ExpiryOle);

        // An expired vehicle counts from "now", not from its old expiry.
        var later = extended!.Single(v => v.RidingId == 90190000).ExpiryOle + 50;
        var again = await repo.TryExtendTimedAsync(id, 90190000, 3, later);
        Assert.Equal((double)(float)(later + 3), again!.Single(v => v.RidingId == 90190000).ExpiryOle);
    }

    [DockerFact]
    public async Task LoadAndCut_stores_the_cut_list_only_when_it_differs_and_keeps_the_stored_order()
    {
        var repo = Repo();
        var id = NewChar();
        var fractional = 46223.57421875;                       // :xx.5 s
        var whole = VehicleRules.WholeSecondExpiry(fractional);
        Seed(id, new BsonArray { Element(90280000, fractional), Element(90150000, 0) }, new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc));

        var cut = await repo.LoadAndCutToWholeSecondsAsync(id);

        Assert.Equal(new[] { 90280000u, 90150000u }, cut.Select(v => v.RidingId));                // the cut does NOT sort
        Assert.Equal(whole, cut[0].ExpiryOle);
        Assert.Equal(0d, cut[1].ExpiryOle);
        var raw = One(id);
        Assert.Equal(whole, raw["Vehicles"][0]["ExpiryOle"].AsDouble);                            // stored
        Assert.Equal(new[] { 90280000, 90150000 }, raw["Vehicles"].AsBsonArray.Select(v => v["RidingId"].AsInt32).ToArray());
        var stamp = raw["UpdatedUtc"].ToUniversalTime();
        Assert.True(stamp > new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc));

        // Already whole: a second load stores nothing (the stamp is untouched).
        await repo.LoadAndCutToWholeSecondsAsync(id);
        Assert.Equal(stamp, One(id)["UpdatedUtc"].ToUniversalTime());
    }

    // ------------------------------------------------------------------------------------------------ failures: propagate vs swallow

    [DockerFact]
    public async Task A_store_failure_propagates_from_grant_and_extend_but_LoadAndCut_swallows_it_and_returns_the_cut_list_unstored()
    {
        // A collection validator that rejects every write the repository makes (its UpdatedUtc is "now") but not the seeded 1999 stamps.
        const string db = "ro2_it_vehicles_validated";
        Assert.True(_fx.Available, "Docker is up but the mongo:7 fixture did not start: " + _fx.StartupError);
        await _fx.Client!.DropDatabaseAsync(db);
        await _fx.Client.GetDatabase(db).CreateCollectionAsync(Coll, new CreateCollectionOptions<BsonDocument>
        {
            Validator = new BsonDocumentFilterDefinition<BsonDocument>(
                new BsonDocument("UpdatedUtc", new BsonDocument("$lt", new BsonDateTime(new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc))))),
        });
        var repo = Repo(db);
        var id = NewChar();
        var fractional = 46223.57421875;
        Seed(id, new BsonArray { Element(90190000, fractional), Element(90280000, 46300.5) }, new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc), db);

        // Not a DuplicateKey, so it is not a lost race: it propagates (Grant, Extend, and the insert path of a first grant).
        var ex = await Assert.ThrowsAsync<MongoWriteException>(() => repo.GrantAsync(id, 90150000, null, Now));
        Assert.Equal(121, ex.WriteError.Code);
        await Assert.ThrowsAsync<MongoWriteException>(() => repo.TryExtendTimedAsync(id, 90190000, 5, Now));
        await Assert.ThrowsAsync<MongoWriteException>(() => repo.GrantAsync(NewChar(), 90150000, null, Now));

        // LoadAndCut swallows it and hands back the cut list, unstored.
        var cut = await repo.LoadAndCutToWholeSecondsAsync(id);
        Assert.Equal(VehicleRules.WholeSecondExpiry(fractional), cut[0].ExpiryOle);
        Assert.Equal(46300.5, cut[1].ExpiryOle);
        var raw = One(id, db);
        Assert.Equal(fractional, raw["Vehicles"][0]["ExpiryOle"].AsDouble);
        Assert.Equal(2, raw["Vehicles"].AsBsonArray.Count);

        // A cancelled token is NOT swallowed: it fails the read first.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repo.LoadAndCutToWholeSecondsAsync(id, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repo.GrantAsync(id, 90150000, null, Now, cts.Token));
    }

    // ------------------------------------------------------------------------------------------------ the uint -> Int32 bound

    [DockerFact]
    public async Task Uint_ids_above_int_max_throw_and_int_max_is_the_last_good_id()
    {
        var repo = Repo();

        // int.MaxValue round-trips as an Int32 (character id in _id, riding id in the element).
        var okChar = (uint)int.MaxValue;
        await repo.GrantAsync(okChar, int.MaxValue, null, Now);
        var raw = Raw().Find(new BsonDocument("_id", int.MaxValue)).Single();
        Assert.Equal(BsonType.Int32, raw["_id"].BsonType);
        Assert.Equal(int.MaxValue, raw["Vehicles"][0]["RidingId"].AsInt32);
        Assert.Equal((uint)int.MaxValue, Assert.Single(await repo.GetAsync(okChar)).RidingId);

        // A riding id above it sits in the DOCUMENT: BsonSerializationException wrapping the OverflowException, nothing stored.
        var id = NewChar();
        var bad = await Assert.ThrowsAsync<BsonSerializationException>(() => repo.GrantAsync(id, 3_000_000_000u, null, Now));
        var inner = bad.InnerException;
        while (inner is BsonSerializationException) inner = inner.InnerException;                  // wrapped once per nesting level (element, then document)
        Assert.IsType<OverflowException>(inner);
        Assert.Equal(0, Count(id));

        // A character id above it sits in the FILTER: the raw OverflowException, on a read and on a write alike.
        await Assert.ThrowsAsync<OverflowException>(() => repo.GetAsync(3_000_000_000u));
        await Assert.ThrowsAsync<OverflowException>(() => repo.GrantAsync(3_000_000_000u, 90190000, null, Now));
        await Assert.ThrowsAsync<OverflowException>(() => repo.LoadAndCutToWholeSecondsAsync(3_000_000_000u));
    }
}
