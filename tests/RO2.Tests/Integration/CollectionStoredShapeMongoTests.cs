using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Domain.Entities;
using RO2.GameData;
using RO2.GameData.Tables;
using RO2.Persistence.Mongo.Persistence;
using RO2.Persistence.Mongo.Repositories;
using RO2.Server.Zone.Services;
using Xunit;
using Xunit.Abstractions;

namespace RO2.Tests.Integration;

/// <summary>
/// B10 S1: the FIRST Mongo twin of the Collections documents (<see cref="CollectionDocument"/>,
/// <see cref="MonsterCollectionProgressDocument"/> and the repository-private pet-incubator document), against a REAL mongo:7
/// container (the <see cref="MongoMailFixture"/> of the Mail twin, a different database). It pins the stored shape (collection names,
/// PascalCase field names, <c>Id -&gt; _id</c>, BSON types, list order, defaults, the legacy-document load, the DateTime round trip, the
/// uint-&gt;Int32 bound) and the rules of the three Mongo repositories, so a byte-identical move of the entities or the repositories keeps
/// it green. Every value below was observed from the current code, not derived. Every test reports SKIPPED without Docker and asserts the
/// fixture started when Docker is up.
/// </summary>
[Collection("MongoMail")]
public sealed class CollectionStoredShapeMongoTests
{
    private const string Db = "ro2_it_collections";
    private static readonly DateTime Base = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
    private static long _charSeq = Random.Shared.Next(1_000_000, 1_000_000_000);

    private readonly MongoMailFixture _fx;
    private readonly ITestOutputHelper _out;
    public CollectionStoredShapeMongoTests(MongoMailFixture fx, ITestOutputHelper output) { _fx = fx; _out = output; }

    // Mongo's UInt32Serializer writes Int32 and throws above int.MaxValue (see Uint_ids_above_int_max_throw...), so every id used
    // as a stored value stays within int.MaxValue.
    private static uint NewChar() => (uint)Interlocked.Increment(ref _charSeq);

    private MongoDbContext Context()
    {
        Assert.True(_fx.Available, "Docker is up but the mongo:7 fixture did not start: " + _fx.StartupError);
        return new MongoDbContext(_fx.ConnectionString, Db);
    }

    private IMongoCollection<BsonDocument> Raw(string name) => _fx.Client!.GetDatabase(Db).GetCollection<BsonDocument>(name);

    private static BsonDocument One(IMongoCollection<BsonDocument> raw, uint id)
        => raw.Find(Builders<BsonDocument>.Filter.Eq("_id", (int)id)).Single();

    private static int[] Ints(BsonValue v) => v.AsBsonArray.Select(x => x.AsInt32).ToArray();

    // ------------------------------------------------------------------------------------------------ proof it ran

    [DockerFact]
    public async Task Ran_against_a_real_mongo7_container()
    {
        Assert.True(_fx.Available, "Docker is up but the mongo:7 fixture did not start: " + _fx.StartupError);
        var info = await _fx.Client!.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument("buildInfo", 1));
        var version = info["version"].AsString;
        _out.WriteLine($"CollectionStoredShapeMongoTests: MongoMailFixture.Available=True mongod={version} at {_fx.ConnectionString}");
        Assert.StartsWith("7.", version);
    }

    // ------------------------------------------------------------------------------------------------ collections book

    [DockerFact]
    public async Task The_collection_book_every_field_round_trips_and_the_document_shape_is_pinned()
    {
        var repo = new CollectionRepository(Context());
        var id = NewChar();
        var doc = new CollectionDocument
        {
            Id = id,
            RegisteredMonsterIds = { 20001, 20003, 20002 },                 // list order is NOT sorted by the store
            RegisteredPetIds = { 83000019, 83000005 },
            SelectedMonsterSkills = { 20003 },
            SelectedPetSkills = { 83000005, 83000019 },
            ActivePetId = 83000019,
            ActivePetFlag = 1,
            PetLoyalties = { new PetLoyalty { PetId = 83000019, Loyalty = 500 }, new PetLoyalty { PetId = 83000005, Loyalty = 10 } },
        };
        await repo.SaveAsync(doc);

        var raw = One(Raw("collections"), id);
        Assert.Equal(new[]
        {
            "_id", "RegisteredMonsterIds", "RegisteredPetIds", "SelectedMonsterSkills", "SelectedPetSkills", "ActivePetId",
            "ActivePetFlag", "PetLoyalties",
        }, raw.Names.ToArray());                                            // PascalCase, Id -> _id, declaration order, no computed members

        Assert.Equal(BsonType.Int32, raw["_id"].BsonType);                  // uint character id -> Int32
        Assert.Equal((int)id, raw["_id"].AsInt32);
        Assert.Equal(new[] { 20001, 20003, 20002 }, Ints(raw["RegisteredMonsterIds"]));
        Assert.Equal(new[] { 83000019, 83000005 }, Ints(raw["RegisteredPetIds"]));
        Assert.Equal(new[] { 20003 }, Ints(raw["SelectedMonsterSkills"]));
        Assert.Equal(new[] { 83000005, 83000019 }, Ints(raw["SelectedPetSkills"]));
        Assert.Equal(BsonType.Int32, raw["ActivePetId"].BsonType);
        Assert.Equal(83000019, raw["ActivePetId"].AsInt32);
        Assert.Equal(BsonType.Int32, raw["ActivePetFlag"].BsonType);        // byte -> Int32
        Assert.Equal(1, raw["ActivePetFlag"].AsInt32);

        var loyalties = raw["PetLoyalties"].AsBsonArray;
        Assert.Equal(2, loyalties.Count);
        Assert.Equal(new[] { "PetId", "Loyalty" }, loyalties[0].AsBsonDocument.Names.ToArray());
        Assert.Equal(BsonType.Int32, loyalties[0]["PetId"].BsonType);
        Assert.Equal(BsonType.Int32, loyalties[0]["Loyalty"].BsonType);     // PetLoyalty.Loyalty is an int
        Assert.Equal((83000019, 500), (loyalties[0]["PetId"].AsInt32, loyalties[0]["Loyalty"].AsInt32));
        Assert.Equal((83000005, 10), (loyalties[1]["PetId"].AsInt32, loyalties[1]["Loyalty"].AsInt32));

        var got = await repo.GetOrCreateAsync(id);
        Assert.Equal(id, got.Id);
        Assert.Equal(new uint[] { 20001, 20003, 20002 }, got.RegisteredMonsterIds);
        Assert.Equal(new uint[] { 83000019, 83000005 }, got.RegisteredPetIds);
        Assert.Equal(new uint[] { 20003 }, got.SelectedMonsterSkills);
        Assert.Equal(new uint[] { 83000005, 83000019 }, got.SelectedPetSkills);
        Assert.Equal(83000019u, got.ActivePetId);
        Assert.Equal((byte)1, got.ActivePetFlag);                           // the REPOSITORY returns the stored flag; the service re-derives it
        Assert.Equal(new[] { (83000019u, 500), (83000005u, 10) }, got.PetLoyalties.Select(p => (p.PetId, p.Loyalty)).ToArray());
        Assert.Equal((ushort)500, got.LoyaltyOf(83000019));
    }

    [DockerFact]
    public async Task A_default_document_stores_empty_arrays_and_zeroes_and_an_unknown_character_is_not_stored_by_a_read()
    {
        var repo = new CollectionRepository(Context());
        var id = NewChar();

        var fresh = await repo.GetOrCreateAsync(id);                        // never stored
        Assert.Equal(id, fresh.Id);
        Assert.Empty(fresh.RegisteredMonsterIds);
        Assert.Empty(fresh.PetLoyalties);
        Assert.Equal(0, await Raw("collections").CountDocumentsAsync(new BsonDocument("_id", (int)id)));   // a read upserts nothing

        await repo.SaveAsync(fresh);
        var raw = One(Raw("collections"), id);
        foreach (var f in new[] { "RegisteredMonsterIds", "RegisteredPetIds", "SelectedMonsterSkills", "SelectedPetSkills", "PetLoyalties" })
        {
            Assert.Equal(BsonType.Array, raw[f].BsonType);
            Assert.Empty(raw[f].AsBsonArray);                               // empty list stored as [], never null / absent
        }
        Assert.Equal(0, raw["ActivePetId"].AsInt32);
        Assert.Equal(0, raw["ActivePetFlag"].AsInt32);
    }

    [DockerFact]
    public async Task Save_is_an_upsert_then_a_whole_document_replace_and_characters_are_isolated()
    {
        var repo = new CollectionRepository(Context());
        var a = NewChar();
        var b = NewChar();

        await repo.SaveAsync(new CollectionDocument { Id = a, RegisteredMonsterIds = { 1, 2, 3 }, ActivePetId = 9 });
        await repo.SaveAsync(new CollectionDocument { Id = b, RegisteredPetIds = { 7 } });
        Assert.Equal(1, await Raw("collections").CountDocumentsAsync(new BsonDocument("_id", (int)a)));

        // A second save is ReplaceOne: the old lists and ActivePetId are gone, not merged.
        await repo.SaveAsync(new CollectionDocument { Id = a, RegisteredPetIds = { 4 } });
        Assert.Equal(1, await Raw("collections").CountDocumentsAsync(new BsonDocument("_id", (int)a)));
        var got = await repo.GetOrCreateAsync(a);
        Assert.Empty(got.RegisteredMonsterIds);
        Assert.Equal(new uint[] { 4 }, got.RegisteredPetIds);
        Assert.Equal(0u, got.ActivePetId);
        Assert.Equal(new uint[] { 7 }, (await repo.GetOrCreateAsync(b)).RegisteredPetIds);
    }

    [DockerFact]
    public async Task Lists_keep_duplicates_and_insertion_order_nothing_is_deduplicated_by_the_store()
    {
        var repo = new CollectionRepository(Context());
        var id = NewChar();
        await repo.SaveAsync(new CollectionDocument { Id = id, RegisteredMonsterIds = { 5, 5, 1, 5 }, SelectedPetSkills = { 3, 3 } });

        var got = await repo.GetOrCreateAsync(id);
        Assert.Equal(new uint[] { 5, 5, 1, 5 }, got.RegisteredMonsterIds);
        Assert.Equal(new uint[] { 3, 3 }, got.SelectedPetSkills);
    }

    [DockerFact]
    public async Task A_legacy_book_without_PetLoyalties_and_the_newer_fields_loads_with_defaults_and_ignores_unknown_fields()
    {
        var repo = new CollectionRepository(Context());
        var id = NewChar();
        await Raw("collections").InsertOneAsync(new BsonDocument     // the shape before PetLoyalties / ActivePetFlag / SelectedPetSkills existed
        {
            { "_id", (int)id },
            { "RegisteredMonsterIds", new BsonArray { 11, 12 } },
            { "RegisteredPetIds", new BsonArray { 83000005 } },
            { "ActivePetId", 83000005 },
            { "SomeFutureField", "ignored" },                        // IgnoreExtraElements: no throw
        });

        var got = await repo.GetOrCreateAsync(id);
        Assert.Equal(new uint[] { 11, 12 }, got.RegisteredMonsterIds);
        Assert.Equal(83000005u, got.ActivePetId);
        Assert.Empty(got.SelectedMonsterSkills);                     // a missing element keeps the initialiser
        Assert.Empty(got.SelectedPetSkills);
        Assert.Empty(got.PetLoyalties);
        Assert.Equal((byte)0, got.ActivePetFlag);
        Assert.Equal((ushort)0, got.LoyaltyOf(83000005));            // booked but never fed: loyalty 0
    }

    [DockerFact]
    public async Task A_stored_ActivePetFlag_is_never_trusted_the_service_re_derives_it_from_the_stored_loyalty()
    {
        var ctx = Context();
        var repo = new CollectionRepository(ctx);
        var id = NewChar();
        const uint pet = 83000019;
        await repo.SaveAsync(new CollectionDocument
        {
            Id = id, RegisteredPetIds = { pet }, ActivePetId = pet, ActivePetFlag = 7,    // a bogus stored flag
            PetLoyalties = { new PetLoyalty { PetId = pet, Loyalty = 499 } },
        });
        Assert.Equal(7, One(Raw("collections"), id)["ActivePetFlag"].AsInt32);

        var withPetData = await StoreAsync(Pet(pet, cap: 500));
        var svc = new CollectionService(repo, withPetData, NullLogger.Instance);
        var below = await svc.GetStateAsync(id);
        Assert.Equal((byte)0, below.ActivePetFlag);                  // 499 < 500: level 0, not the stored 7

        await repo.SaveAsync(new CollectionDocument
        {
            Id = id, RegisteredPetIds = { pet }, ActivePetId = pet, ActivePetFlag = 0,
            PetLoyalties = { new PetLoyalty { PetId = pet, Loyalty = 500 } },
        });
        Assert.Equal((byte)1, (await svc.GetStateAsync(id)).ActivePetFlag);   // 500 >= LoyaltyLevel_1: level 1, not the stored 0
        Assert.Equal(0, One(Raw("collections"), id)["ActivePetFlag"].AsInt32);   // GetState re-derives in memory, it does not write

        // Without pet data (store null) the derived flag is 0 whatever is stored.
        var noData = new CollectionService(repo, null, NullLogger.Instance);
        Assert.Equal((byte)0, (await noData.GetStateAsync(id)).ActivePetFlag);
    }

    [DockerFact]
    public async Task A_service_write_then_read_through_the_real_repository_persists_register_select_feed_and_activate()
    {
        var ctx = Context();
        var repo = new CollectionRepository(ctx);
        var id = NewChar();
        const uint pet = 83000019;
        var svc = new CollectionService(repo, await StoreAsync(Pet(pet, cap: 25)), NullLogger.Instance);

        Assert.Equal(1, await svc.RegisterPetAsync(id, pet));
        Assert.Equal(1, await svc.RegisterPetAsync(id, pet));        // idempotent
        Assert.Equal((ushort)10, await svc.FeedPetAsync(id, pet));
        Assert.Equal((ushort)20, await svc.FeedPetAsync(id, pet));
        Assert.Equal((ushort)25, await svc.FeedPetAsync(id, pet));   // min(loyalty + 10, cap)
        Assert.Null(await svc.FeedPetAsync(id, pet));                // at the cap: nothing fed
        Assert.Equal((pet, (byte)1), await svc.SetActivePetAsync(id, pet));

        var raw = One(Raw("collections"), id);
        Assert.Equal(new[] { (int)pet }, Ints(raw["RegisteredPetIds"]));
        Assert.Equal(25, raw["PetLoyalties"][0]["Loyalty"].AsInt32);
        Assert.Equal((int)pet, raw["ActivePetId"].AsInt32);
        Assert.Equal(1, raw["ActivePetFlag"].AsInt32);               // SetActivePet DOES store the derived flag
    }

    // ------------------------------------------------------------------------------------------------ monster progress

    [DockerFact]
    public async Task Monster_progress_shape_entries_keep_list_order_and_Find_reads_them()
    {
        var repo = new MonsterCollectionProgressRepository(Context());
        var id = NewChar();
        Assert.Null(await repo.GetAsync(id));                        // unknown character: null, not a default document

        await repo.SaveAsync(new MonsterCollectionProgressDocument
        {
            Id = id,
            Entries =
            {
                new MonsterCollectionEntry { NpcId = 20003, Level = 4, Exp = 0 },
                new MonsterCollectionEntry { NpcId = 20001, Level = 2, Exp = 7 },
                new MonsterCollectionEntry { NpcId = 20002, Level = 1, Exp = 2_000_000_000u },
            },
        });

        var raw = One(Raw("collection_monster_progress"), id);
        Assert.Equal(new[] { "_id", "Entries" }, raw.Names.ToArray());
        var entries = raw["Entries"].AsBsonArray;
        Assert.Equal(new[] { "NpcId", "Level", "Exp" }, entries[0].AsBsonDocument.Names.ToArray());
        foreach (var f in new[] { "NpcId", "Level", "Exp" }) Assert.Equal(BsonType.Int32, entries[0][f].BsonType);   // uint, byte, uint -> Int32
        Assert.Equal(new[] { 20003, 20001, 20002 }, entries.Select(e => e["NpcId"].AsInt32).ToArray());     // list order, not sorted
        Assert.Equal(new[] { 4, 2, 1 }, entries.Select(e => e["Level"].AsInt32).ToArray());
        Assert.Equal(new[] { 0, 7, 2_000_000_000 }, entries.Select(e => e["Exp"].AsInt32).ToArray());

        var got = (await repo.GetAsync(id))!;
        Assert.Equal(id, got.Id);
        Assert.Equal(new uint[] { 20003, 20001, 20002 }, got.Entries.Select(e => e.NpcId).ToArray());
        Assert.Equal((byte)2, got.Find(20001)!.Level);
        Assert.Equal(7u, got.Find(20001)!.Exp);
        Assert.Equal(2_000_000_000u, got.Find(20002)!.Exp);
        Assert.Null(got.Find(99999));
    }

    [DockerFact]
    public async Task Monster_progress_save_is_an_upsert_then_a_whole_document_replace_and_an_empty_document_stores_an_empty_array()
    {
        var repo = new MonsterCollectionProgressRepository(Context());
        var a = NewChar();
        var b = NewChar();
        await repo.SaveAsync(new MonsterCollectionProgressDocument { Id = a, Entries = { new() { NpcId = 1, Level = 3, Exp = 1 } } });
        await repo.SaveAsync(new MonsterCollectionProgressDocument { Id = b, Entries = { new() { NpcId = 2 } } });
        await repo.SaveAsync(new MonsterCollectionProgressDocument { Id = a });          // replace with an empty document

        Assert.Equal(1, await Raw("collection_monster_progress").CountDocumentsAsync(new BsonDocument("_id", (int)a)));
        var raw = One(Raw("collection_monster_progress"), a);
        Assert.Equal(BsonType.Array, raw["Entries"].BsonType);
        Assert.Empty(raw["Entries"].AsBsonArray);
        Assert.Empty((await repo.GetAsync(a))!.Entries);
        Assert.Equal(new uint[] { 2 }, (await repo.GetAsync(b))!.Entries.Select(e => e.NpcId).ToArray());
    }

    [DockerFact]
    public async Task A_legacy_monster_progress_entry_without_Level_loads_at_level_1_and_a_document_without_Entries_loads_empty()
    {
        var repo = new MonsterCollectionProgressRepository(Context());
        var withEntry = NewChar();
        var bare = NewChar();
        await Raw("collection_monster_progress").InsertManyAsync(new[]
        {
            new BsonDocument { { "_id", (int)withEntry }, { "Entries", new BsonArray { new BsonDocument { { "NpcId", 20001 }, { "Surprise", 1 } } } } },
            new BsonDocument { { "_id", (int)bare } },
        });

        var e = (await repo.GetAsync(withEntry))!.Entries.Single();
        Assert.Equal(20001u, e.NpcId);
        Assert.Equal((byte)1, e.Level);                              // the property initialiser (1), the unprogressed default
        Assert.Equal(0u, e.Exp);
        Assert.Empty((await repo.GetAsync(bare))!.Entries);
    }

    // ------------------------------------------------------------------------------------------------ pet incubators

    private static PetIncubatorRepository Incubators(MongoDbContext ctx) => new(ctx);

    [DockerFact]
    public async Task The_incubator_document_shape_slots_keep_list_order_and_dates_are_bson_dates_at_ms_precision()
    {
        var repo = Incubators(Context());
        var id = NewChar();
        Assert.Empty(await repo.GetSlotsAsync(id));                  // unknown character: empty, nothing stored
        Assert.Equal(0, await Raw("pet_incubators").CountDocumentsAsync(new BsonDocument("_id", (int)id)));

        var now = Base.AddMilliseconds(123).AddTicks(4567);          // 123.4567 ms
        Assert.True(await repo.TryPlaceIncubatorAsync(id, 1, 22402103, 1, now));            // slot 1 FIRST
        Assert.True(await repo.TryPlaceIncubatorAsync(id, 0, 22402101, 2, now));
        Assert.True(await repo.TryStartIncubationAsync(id, 0, 22402101, 22402139, now, now.AddSeconds(12600)));

        var raw = One(Raw("pet_incubators"), id);
        Assert.Equal(new[] { "_id", "Slots" }, raw.Names.ToArray());
        var slots = raw["Slots"].AsBsonArray;
        Assert.Equal(new[] { "Slot", "IncubatorId", "Charges", "EggId", "StartUtc", "EndUtc" }, slots[0].AsBsonDocument.Names.ToArray());
        Assert.Equal(new[] { 1, 0 }, slots.Select(s => s["Slot"].AsInt32).ToArray());       // insertion order, not slot order
        foreach (var f in new[] { "Slot", "IncubatorId", "Charges", "EggId" }) Assert.Equal(BsonType.Int32, slots[1][f].BsonType);   // byte/uint -> Int32
        Assert.Equal(BsonType.DateTime, slots[1]["StartUtc"].BsonType);
        Assert.Equal(BsonType.DateTime, slots[1]["EndUtc"].BsonType);
        Assert.Equal((22402101, 2, 22402139), (slots[1]["IncubatorId"].AsInt32, slots[1]["Charges"].AsInt32, slots[1]["EggId"].AsInt32));
        Assert.Equal(new DateTimeOffset(Base.AddMilliseconds(123)).ToUnixTimeMilliseconds(), slots[1]["StartUtc"].AsBsonDateTime.MillisecondsSinceEpoch);   // 0.4567 ms truncated
        Assert.Equal(new DateTimeOffset(Base.AddMilliseconds(123).AddSeconds(12600)).ToUnixTimeMilliseconds(), slots[1]["EndUtc"].AsBsonDateTime.MillisecondsSinceEpoch);
        Assert.Equal(0, slots[0]["EggId"].AsInt32);                  // the empty slot stores EggId 0

        var got = await repo.GetSlotsAsync(id);
        Assert.Equal(new byte[] { 1, 0 }, got.Select(s => s.Slot).ToArray());                // list order is preserved on read
        Assert.Equal(Base.AddMilliseconds(123), got[1].StartUtc);
        Assert.Equal(Base.AddMilliseconds(123).AddSeconds(12600), got[1].EndUtc);
        Assert.All(got, s => Assert.Equal(DateTimeKind.Utc, s.StartUtc.Kind));
        Assert.All(got, s => Assert.Equal(DateTimeKind.Utc, s.EndUtc.Kind));
        Assert.Equal(got[0].StartUtc, got[0].EndUtc);                // an empty slot: End == Start
    }

    [DockerFact]
    public async Task Incubator_place_refusals_store_nothing_and_the_slot_count_is_two()
    {
        var repo = Incubators(Context());
        var id = NewChar();

        Assert.False(await repo.TryPlaceIncubatorAsync(id, 2, 22402101, 2, Base));          // slot index >= SlotCount
        Assert.False(await repo.TryPlaceIncubatorAsync(id, 255, 22402101, 2, Base));
        Assert.False(await repo.TryPlaceIncubatorAsync(id, 0, 22402101, 0, Base));          // 0 charges
        Assert.Equal(0, await Raw("pet_incubators").CountDocumentsAsync(new BsonDocument("_id", (int)id)));   // a refusal does not even create the document

        Assert.True(await repo.TryPlaceIncubatorAsync(id, 0, 22402101, 2, Base));
        Assert.False(await repo.TryPlaceIncubatorAsync(id, 0, 22402103, 1, Base));          // occupied
        var s = (await repo.GetSlotsAsync(id)).Single();
        Assert.Equal((22402101u, (byte)2), (s.IncubatorId, s.Charges));                     // the original is kept
        Assert.Equal(PetIncubatorRules.SlotCount, 2);
    }

    [DockerFact]
    public async Task Incubator_start_and_collect_follow_the_rules_and_the_last_charge_removes_the_slot_but_keeps_the_document()
    {
        var repo = Incubators(Context());
        var id = NewChar();
        const uint inc = 22402103;
        const uint egg = 22402139;
        await repo.TryPlaceIncubatorAsync(id, 0, inc, 1, Base);

        Assert.False(await repo.TryStartIncubationAsync(id, 1, inc, egg, Base, Base.AddHours(1)));    // no such slot
        Assert.False(await repo.TryStartIncubationAsync(id, 0, 22402101, egg, Base, Base.AddHours(1))); // wrong incubator
        Assert.False(await repo.TryStartIncubationAsync(id, 0, inc, 0, Base, Base.AddHours(1)));       // egg 0
        Assert.True(await repo.TryStartIncubationAsync(id, 0, inc, egg, Base, Base.AddHours(1)));
        Assert.False(await repo.TryStartIncubationAsync(id, 0, inc, egg, Base, Base.AddHours(2)));     // already holds an egg
        Assert.Equal((byte)1, (await repo.GetSlotsAsync(id)).Single().Charges);             // charges do not move while an egg is in

        Assert.Null(await repo.TryCollectAsync(id, 0, inc, Base.AddHours(1).AddMilliseconds(-1)));     // one ms before it hatches
        Assert.Null(await repo.TryCollectAsync(id, 0, 22402101, Base.AddHours(2)));                    // wrong incubator
        Assert.Null(await repo.TryCollectAsync(id, 1, inc, Base.AddHours(2)));                         // empty slot index

        var before = await repo.TryCollectAsync(id, 0, inc, Base.AddHours(1));                         // exactly at EndUtc: allowed
        Assert.NotNull(before);
        Assert.Equal((inc, egg, (byte)1), (before!.IncubatorId, before.EggId, before.Charges));        // the slot as it was
        Assert.Empty(await repo.GetSlotsAsync(id));                                          // 1 charge -> 0 -> the slot is removed
        var raw = One(Raw("pet_incubators"), id);
        Assert.Empty(raw["Slots"].AsBsonArray);                                              // the document stays with an empty array

        Assert.True(await repo.TryPlaceIncubatorAsync(id, 0, inc, 1, Base));                 // the freed slot takes a new incubator
    }

    [DockerFact]
    public async Task Incubator_collect_with_charges_left_empties_the_slot_and_Restore_moves_it_to_the_end_of_the_list()
    {
        var repo = Incubators(Context());
        var id = NewChar();
        await repo.TryPlaceIncubatorAsync(id, 0, 22402101, 2, Base);
        await repo.TryPlaceIncubatorAsync(id, 1, 22402103, 1, Base);
        await repo.TryStartIncubationAsync(id, 0, 22402101, 22402139, Base, Base.AddHours(1));

        var collectedAt = Base.AddHours(2);
        var before = (await repo.TryCollectAsync(id, 0, 22402101, collectedAt))!;
        var after = await repo.GetSlotsAsync(id);
        var s0 = after.Single(s => s.Slot == 0);
        Assert.Equal((0u, (byte)1), (s0.EggId, s0.Charges));                                 // egg gone, one charge taken
        Assert.Equal((collectedAt, collectedAt), (s0.StartUtc, s0.EndUtc));                  // start = end = the collect instant

        await repo.RestoreSlotAsync(id, before);                                             // the compensation of a failed bag landing
        var restored = await repo.GetSlotsAsync(id);
        Assert.Equal(new byte[] { 1, 0 }, restored.Select(s => s.Slot).ToArray());           // removed and re-added: now LAST
        var r0 = restored.Single(s => s.Slot == 0);
        Assert.Equal((22402101u, 22402139u, (byte)2), (r0.IncubatorId, r0.EggId, r0.Charges));
        Assert.Equal(Base.AddHours(1), r0.EndUtc);

        // Restore on a character with no document creates it.
        var other = NewChar();
        await repo.RestoreSlotAsync(other, before);
        Assert.Single(await repo.GetSlotsAsync(other));
    }

    [DockerFact]
    public async Task A_legacy_incubator_document_without_Slots_loads_empty_and_characters_are_isolated()
    {
        var repo = Incubators(Context());
        var bare = NewChar();
        var a = NewChar();
        var b = NewChar();
        await Raw("pet_incubators").InsertOneAsync(new BsonDocument { { "_id", (int)bare }, { "SomeFutureField", 1 } });
        Assert.Empty(await repo.GetSlotsAsync(bare));                                        // missing Slots keeps the initialiser; extra fields are ignored

        await repo.TryPlaceIncubatorAsync(a, 0, 22402101, 2, Base);
        Assert.Empty(await repo.GetSlotsAsync(b));
        // The first write to the legacy document keeps working (the whole document is replaced, the unknown field is dropped).
        Assert.True(await repo.TryPlaceIncubatorAsync(bare, 0, 22402101, 2, Base));
        Assert.Equal(new[] { "_id", "Slots" }, One(Raw("pet_incubators"), bare).Names.ToArray());
    }

    // ------------------------------------------------------------------------------------------------ the uint -> Int32 bound

    [DockerFact]
    public async Task Uint_ids_above_int_max_throw_on_save_and_int_max_is_the_last_good_id()
    {
        var ctx = Context();

        // Confirmed on a raw BsonDocument: Int32 is the type the driver writes for a uint, and an id above int.MaxValue cannot be one.
        var okId = (uint)int.MaxValue;
        await new CollectionRepository(ctx).SaveAsync(new CollectionDocument { Id = okId });
        Assert.Equal(BsonType.Int32, Raw("collections").Find(new BsonDocument("_id", int.MaxValue)).Single()["_id"].BsonType);

        // The id sits in the ReplaceOne FILTER (d.Id == doc.Id): the driver converts it while translating the filter, so a too-big id
        // surfaces as the raw OverflowException, before any document is serialised.
        await Assert.ThrowsAsync<OverflowException>(() =>
            new CollectionRepository(ctx).SaveAsync(new CollectionDocument { Id = (uint)int.MaxValue + 1 }));
        await Assert.ThrowsAsync<OverflowException>(() =>
            new MonsterCollectionProgressRepository(ctx).SaveAsync(new MonsterCollectionProgressDocument { Id = 3_000_000_000u }));
        await Assert.ThrowsAsync<OverflowException>(() => new CollectionRepository(ctx).GetOrCreateAsync(3_000_000_000u));   // a Find filter too
        await Assert.ThrowsAsync<OverflowException>(() => new MonsterCollectionProgressRepository(ctx).GetAsync(3_000_000_000u));
        await Assert.ThrowsAsync<OverflowException>(() => Incubators(ctx).GetSlotsAsync(3_000_000_000u));

        // A too-big uint inside the DOCUMENT (a list element, a slot field) is a BsonSerializationException wrapping the OverflowException.
        var bad = await Assert.ThrowsAsync<BsonSerializationException>(() =>
            new CollectionRepository(ctx).SaveAsync(new CollectionDocument { Id = NewChar(), RegisteredMonsterIds = { 3_000_000_000u } }));
        Assert.IsType<OverflowException>(bad.InnerException);
        await Assert.ThrowsAsync<BsonSerializationException>(() =>
            Incubators(ctx).TryPlaceIncubatorAsync(NewChar(), 0, 3_000_000_000u, 2, Base));
        await Assert.ThrowsAsync<BsonSerializationException>(() =>
            new MonsterCollectionProgressRepository(ctx).SaveAsync(new MonsterCollectionProgressDocument
                { Id = NewChar(), Entries = { new MonsterCollectionEntry { NpcId = 1, Exp = 3_000_000_000u } } }));
    }

    // ------------------------------------------------------------------------------------------------ helpers

    private static PetInfo Pet(uint npcId, ushort cap)
        => new() { NPC_ID = npcId, LoyaltyLevel_1 = cap, LoyaltyEffect_0 = 111, LoyaltyEffect_1 = 222 };

    /// <summary>A GameDataStore holding only the given PetInfo rows.</summary>
    private static async Task<GameDataStore> StoreAsync(params PetInfo[] pets)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ro2-b10s1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "PetInfo.json"), System.Text.Json.JsonSerializer.Serialize(pets));
            var store = new GameDataStore();
            await store.LoadAsync(dir);
            return store;
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
