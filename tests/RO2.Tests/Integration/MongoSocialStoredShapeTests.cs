using System.Reflection;
using MongoDB.Bson;
using MongoDB.Driver;
using RO2.Domain.Entities;
using RO2.Characters.Domain;
using RO2.Persistence.Mongo.Persistence;
using RO2.Server.Chat;
using RO2.Socials.Domain;
using RO2.Socials.Infrastructure.Mongo;
using RO2.Tests.Economy;
using Xunit;
using Xunit.Abstractions;

namespace RO2.Tests.Integration;

// B14 S1: the stored-shape net for the two Chat social-graph collections (chat_friend_lists, chat_block_lists), against a REAL mongo:7
// container (MongoMailFixture, one database per class). The raw persistence is exercised through the Mongo repositories behind the S5/S6 ports
// (MongoFriendListRepository / MongoBlockListRepository), the host facades (FriendStore.CountAsync, BlockListRepository.GetAsync) and FriendPresence. Every value below was observed from the current code. The Postgres friend/block repositories and tables do
// not exist yet (S7), so the both-provider round-trip is DEFERRED to S7: S1 pins the MONGO shape only. Every test reports SKIPPED without
// Docker and asserts the fixture started when Docker is up.

/// <summary>The <c>chat_friend_lists</c> collection: <see cref="FriendListDocument"/> (_id = owner Character.Id, FriendIds).</summary>
[Collection("MongoMail")]
public sealed class MongoFriendListStoredShapeTests
{
    private const string Db = "ro2_it_social_friends";
    private const string Coll = "chat_friend_lists";
    private static long _seq = Random.Shared.Next(1_000_000, 1_000_000_000);

    private readonly MongoMailFixture _fx;
    private readonly ITestOutputHelper _out;
    public MongoFriendListStoredShapeTests(MongoMailFixture fx, ITestOutputHelper output) { _fx = fx; _out = output; }

    // Mongo's UInt32Serializer writes Int32 and throws above int.MaxValue, so every uint stored value stays within int.MaxValue.
    private static uint New() => (uint)Interlocked.Increment(ref _seq);

    private (MongoFriendListRepository store, FakeCharacterRepository chars) Open()
    {
        Assert.True(_fx.Available, "Docker is up but the mongo:7 fixture did not start: " + _fx.StartupError);
        var chars = new FakeCharacterRepository(new RepoScheduler());
        return (new MongoFriendListRepository(new MongoDbContext(_fx.ConnectionString, Db)), chars);
    }

    // The IChannelPresenceStore is only used by GetEntriesAsync (needs a presence store); CountAsync never touches it.
    private static FriendStore Facade(MongoFriendListRepository store, FakeCharacterRepository chars) => new(store, chars, null!);

    private IMongoCollection<BsonDocument> Raw() => _fx.Client!.GetDatabase(Db).GetCollection<BsonDocument>(Coll);
    private BsonDocument One(uint id) => Raw().Find(new BsonDocument("_id", (int)id)).Single();

    [DockerFact]
    public async Task Ran_against_a_real_mongo7_container()
    {
        Assert.True(_fx.Available, "Docker is up but the mongo:7 fixture did not start: " + _fx.StartupError);
        var info = await _fx.Client!.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument("buildInfo", 1));
        var version = info["version"].AsString;
        _out.WriteLine($"MongoFriendListStoredShapeTests: MongoMailFixture.Available=True mongod={version} at {_fx.ConnectionString}");
        Assert.StartsWith("7.", version);
    }

    [DockerFact]
    public async Task An_added_friend_list_has_the_pinned_field_names_and_types_with_insertion_order_and_no_discriminator()
    {
        var (store, _) = Open();
        var owner = New();
        Assert.Equal(AddOutcome.Added, await store.AddAsync(owner, 30));
        Assert.Equal(AddOutcome.Added, await store.AddAsync(owner, 10));
        Assert.Equal(AddOutcome.Added, await store.AddAsync(owner, 20));

        var raw = One(owner);
        Assert.Equal(new[] { "_id", "FriendIds" }, raw.Names.ToArray());
        Assert.Equal(BsonType.Int32, raw["_id"].BsonType);                      // uint owner character id -> Int32
        Assert.Equal((int)owner, raw["_id"].AsInt32);
        Assert.Equal(BsonType.Array, raw["FriendIds"].BsonType);
        Assert.All(raw["FriendIds"].AsBsonArray, v => Assert.Equal(BsonType.Int32, v.BsonType));
        Assert.Equal(new[] { 30, 10, 20 }, raw["FriendIds"].AsBsonArray.Select(v => v.AsInt32).ToArray());   // order = added order, not sorted
        Assert.DoesNotContain("\"_t\"", raw.ToJson());
        Assert.Equal(1, Raw().CountDocuments(new BsonDocument("_id", (int)owner)));

        var typed = _fx.Client!.GetDatabase(Db).GetCollection<FriendListDocument>(Coll)
            .Find(d => d.Id == owner).Single();
        Assert.Equal(owner, typed.Id);
        Assert.Equal(new uint[] { 30, 10, 20 }, typed.FriendIds);
    }

    [DockerFact]
    public async Task Add_is_idempotent_AlreadyFriend_and_Remove_pulls_only_a_listed_id()
    {
        var (store, _) = Open();
        var owner = New();
        await store.AddAsync(owner, 5);
        Assert.Equal(AddOutcome.AlreadyFriend, await store.AddAsync(owner, 5));
        Assert.Equal(new[] { 5 }, One(owner)["FriendIds"].AsBsonArray.Select(v => v.AsInt32).ToArray());

        Assert.True(await store.ListsFriendAsync(owner, 5, CancellationToken.None));
        Assert.False(await store.ListsFriendAsync(owner, 6, CancellationToken.None));
        Assert.False(await store.ListsFriendAsync(New(), 5, CancellationToken.None));       // no document at all

        await store.RemoveAsync(owner, 99);                                                   // never listed: nothing written
        Assert.Equal(new[] { 5 }, One(owner)["FriendIds"].AsBsonArray.Select(v => v.AsInt32).ToArray());
        await store.RemoveAsync(owner, 5);
        Assert.Empty(One(owner)["FriendIds"].AsBsonArray);                                    // the document stays, the array empties
    }

    [DockerFact]
    public async Task The_cap_is_enforced_by_the_atomic_guard_on_the_raw_array()
    {
        var (store, _) = Open();
        Assert.Equal(200, FriendStore.MaxFriends);

        var below = New();
        Raw().InsertOne(new BsonDocument { { "_id", (int)below }, { "FriendIds", new BsonArray(Enumerable.Range(1, FriendStore.MaxFriends - 1)) } });
        Assert.Equal(AddOutcome.Added, await store.AddAsync(below, 5000));         // 199 -> 200
        Assert.Equal(FriendStore.MaxFriends, One(below)["FriendIds"].AsBsonArray.Count);

        Assert.Equal(AddOutcome.ListFull, await store.AddAsync(below, 5001));      // 200: full, new id refused
        Assert.Equal(AddOutcome.AlreadyFriend, await store.AddAsync(below, 5000)); // 200: full but already listed -> AlreadyFriend
        Assert.Equal(FriendStore.MaxFriends, One(below)["FriendIds"].AsBsonArray.Count);
    }

    [DockerFact]
    public async Task A_legacy_document_with_extra_fields_or_without_FriendIds_reads_its_defaults()
    {
        var (store, _) = Open();
        var typed = _fx.Client!.GetDatabase(Db).GetCollection<FriendListDocument>(Coll);

        var noArray = New();
        Raw().InsertOne(new BsonDocument { { "_id", (int)noArray }, { "RetiredField", "x" } });   // [BsonIgnoreExtraElements]
        var doc = typed.Find(d => d.Id == noArray).Single();
        Assert.Empty(doc.FriendIds);                                                              // missing field -> empty list, not null
        Assert.False(await store.ListsFriendAsync(noArray, 1, CancellationToken.None));
        Assert.Equal(AddOutcome.Added, await store.AddAsync(noArray, 7));             // $addToSet creates the array
        Assert.Equal(new[] { 7 }, One(noArray)["FriendIds"].AsBsonArray.Select(v => v.AsInt32).ToArray());
        Assert.Equal("x", One(noArray)["RetiredField"].AsString);                                 // the write does not strip unknown fields
    }

    [DockerFact]
    public async Task CountAsync_counts_only_live_characters_while_the_stored_array_keeps_deleted_ids()
    {
        var (store, chars) = Open();
        var owner = New();
        var live = New(); var gone = New();
        chars.Seed(new Character { Id = live, Name = "Live" });
        chars.Seed(new Character { Id = gone, Name = "Gone", IsDeleted = true });
        await store.AddAsync(owner, live);
        await store.AddAsync(owner, gone);

        Assert.Equal(2, One(owner)["FriendIds"].AsBsonArray.Count);                              // raw array keeps both
        var facade = Facade(store, chars);
        Assert.Equal(1, await facade.CountAsync(owner, CancellationToken.None));                 // the 33 pre-check counts the live one only
        Assert.Equal(0, await facade.CountAsync(New(), CancellationToken.None));
    }

    [DockerFact]
    public async Task Uint_ids_above_int_max_throw_so_stored_ids_must_stay_within_int_max()
    {
        var (store, _) = Open();
        await Assert.ThrowsAnyAsync<Exception>(() => store.AddAsync(New(), (uint)int.MaxValue + 1));
        await store.AddAsync(New(), int.MaxValue);                                                // the boundary itself is fine
    }
}

/// <summary>The <c>chat_block_lists</c> collection: <see cref="BlockListDocument"/> (Id -> _id by convention) with its <see cref="BlockListEntry"/> elements.</summary>
[Collection("MongoMail")]
public sealed class MongoBlockListStoredShapeTests
{
    private const string Db = "ro2_it_social_blocks";
    private const string Coll = "chat_block_lists";
    private const ulong OwnerGid = 0x0DE0B6B300000000UL;
    private static long _seq = Random.Shared.Next(1_000_000, 1_000_000_000);

    private readonly MongoMailFixture _fx;
    private readonly ITestOutputHelper _out;
    public MongoBlockListStoredShapeTests(MongoMailFixture fx, ITestOutputHelper output) { _fx = fx; _out = output; }

    private static uint New() => (uint)Interlocked.Increment(ref _seq);

    private (MongoBlockListRepository repo, FakeCharacterRepository chars, MongoDbContext ctx) Open()
    {
        Assert.True(_fx.Available, "Docker is up but the mongo:7 fixture did not start: " + _fx.StartupError);
        var ctx = new MongoDbContext(_fx.ConnectionString, Db);
        var chars = new FakeCharacterRepository(new RepoScheduler());
        return (new MongoBlockListRepository(ctx), chars, ctx);
    }

    // GetAsync is the host's name-refresh join over the raw entries the port returns.
    private static BlockListRepository Facade(MongoBlockListRepository repo, FakeCharacterRepository chars) => new(repo, chars);

    private IMongoCollection<BsonDocument> Raw() => _fx.Client!.GetDatabase(Db).GetCollection<BsonDocument>(Coll);
    private BsonDocument One(uint id) => Raw().Find(new BsonDocument("_id", (int)id)).Single();

    [DockerFact]
    public async Task Ran_against_a_real_mongo7_container()
    {
        Assert.True(_fx.Available, "Docker is up but the mongo:7 fixture did not start: " + _fx.StartupError);
        var info = await _fx.Client!.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument("buildInfo", 1));
        var version = info["version"].AsString;
        _out.WriteLine($"MongoBlockListStoredShapeTests: MongoMailFixture.Available=True mongod={version} at {_fx.ConnectionString}");
        Assert.StartsWith("7.", version);
    }

    [DockerFact]
    public async Task An_added_block_has_the_pinned_field_names_and_types_with_the_bare_id_as_a_64bit_Uid()
    {
        var (repo, _, _) = Open();
        var owner = New();
        Assert.Equal(BlockAddOutcome.Added, await repo.AddAsync(owner, 30, "Cee"));
        Assert.Equal(BlockAddOutcome.Added, await repo.AddAsync(owner, 10, "Aye"));

        var raw = One(owner);
        // BlockListDocument has NO [BsonId]/[BsonIgnoreExtraElements]: Id -> _id by the driver's Id convention; unknown-field tolerance is the
        // process-wide IgnoreExtraElements convention registered by MongoDbContext's static ctor.
        Assert.Equal(new[] { "_id", "Entries" }, raw.Names.ToArray());
        Assert.Equal(BsonType.Int32, raw["_id"].BsonType);                       // uint owner id -> Int32
        Assert.Equal((int)owner, raw["_id"].AsInt32);
        var entries = raw["Entries"].AsBsonArray;
        Assert.Equal(2, entries.Count);
        foreach (var e in entries)
            Assert.Equal(new[] { "Uid", "Name" }, e.AsBsonDocument.Names.ToArray());
        Assert.Equal(BsonType.Int64, entries[0]["Uid"].BsonType);                // ulong Uid -> Int64 (kept 64-bit for the pre-GID rows)
        Assert.Equal(30L, entries[0]["Uid"].AsInt64);                            // the BARE character id, never the GID
        Assert.Equal("Cee", entries[0]["Name"].AsString);
        Assert.Equal(new[] { 30L, 10L }, entries.Select(e => e["Uid"].AsInt64).ToArray());   // added order, not sorted
        Assert.DoesNotContain("\"_t\"", raw.ToJson());
    }

    [DockerFact]
    public async Task Add_refuses_a_duplicate_as_AlreadyRegistered_and_BlocksAsync_and_Remove_work_on_the_bare_id()
    {
        var (repo, _, _) = Open();
        var owner = New();
        await repo.AddAsync(owner, 7, "Seven");
        Assert.Equal(BlockAddOutcome.AlreadyRegistered, await repo.AddAsync(owner, 7, "Renamed"));
        Assert.Single(One(owner)["Entries"].AsBsonArray);
        Assert.Equal("Seven", One(owner)["Entries"][0]["Name"].AsString);        // the stored name is not rewritten

        Assert.True(await repo.BlocksAsync(owner, 7));
        Assert.False(await repo.BlocksAsync(owner, 8));
        Assert.False(await repo.BlocksAsync(New(), 7));

        await repo.RemoveAsync(owner, 99);                                        // never blocked: nothing written
        Assert.Single(One(owner)["Entries"].AsBsonArray);
        await repo.RemoveAsync(owner, OwnerGid | 7);                              // a GID names the same row through its low dword
        Assert.Empty(One(owner)["Entries"].AsBsonArray);
    }

    [DockerFact]
    public async Task The_cap_is_enforced_by_the_atomic_guard()
    {
        var (repo, _, _) = Open();
        Assert.Equal(200, BlockListRepository.MaxBlocked);
        var owner = New();
        Raw().InsertOne(new BsonDocument
        {
            { "_id", (int)owner },
            { "Entries", new BsonArray(Enumerable.Range(1, BlockListRepository.MaxBlocked - 1)
                .Select(i => new BsonDocument { { "Uid", (long)i }, { "Name", "n" + i } })) },
        });
        Assert.Equal(BlockAddOutcome.Added, await repo.AddAsync(owner, 5000, "last"));        // 199 -> 200
        Assert.Equal(BlockAddOutcome.ListFull, await repo.AddAsync(owner, 5001, "over"));
        Assert.Equal(BlockAddOutcome.AlreadyRegistered, await repo.AddAsync(owner, 5000, "last"));
        Assert.Equal(BlockListRepository.MaxBlocked, One(owner)["Entries"].AsBsonArray.Count);
    }

    [DockerFact]
    public async Task GetAsync_projects_stored_ids_to_GIDs_under_the_owner_high_dword_and_refreshes_names()
    {
        var (repo, chars, _) = Open();
        var owner = New();
        var renamed = New(); var vanished = New();
        chars.Seed(new Character { Id = renamed, Name = "NewName" });
        Raw().InsertOne(new BsonDocument
        {
            { "_id", (int)owner },
            { "Entries", new BsonArray
                {
                    new BsonDocument { { "Uid", (long)renamed }, { "Name", "OldName" } },
                    new BsonDocument { { "Uid", (long)vanished }, { "Name", "Stored" } },      // character no longer resolves
                    new BsonDocument { { "Uid", (long)(OwnerGid | renamed) }, { "Name", "Dup" } },
                } },
        });

        var facade = Facade(repo, chars);
        var got = await facade.GetAsync(owner, OwnerGid);
        Assert.Equal(OwnerGid | renamed, got[0].Gid);
        Assert.Equal("NewName", got[0].Name);                                    // name re-read from the characters
        Assert.Equal(OwnerGid | vanished, got[1].Gid);
        Assert.Equal("Stored", got[1].Name);                                     // unresolved -> the stored name
        Assert.Equal(OwnerGid | renamed, got[2].Gid);                            // a row that already holds a GID projects through its low dword
        Assert.Empty(await facade.GetAsync(New(), OwnerGid));
    }

    [DockerFact]
    public async Task A_legacy_document_with_extra_fields_or_without_Entries_reads_its_defaults()
    {
        var (repo, chars, ctx) = Open();
        var typed = _fx.Client!.GetDatabase(Db).GetCollection<BlockListDocument>(Coll);

        var bare = New();
        Raw().InsertOne(new BsonDocument { { "_id", (int)bare }, { "RetiredField", 1 } });
        var doc = typed.Find(d => d.Id == bare).Single();                       // extra field ignored by the process-wide convention
        Assert.Empty(doc.Entries);
        Assert.Empty(await Facade(repo, chars).GetAsync(bare, OwnerGid));
        Assert.Equal(BlockAddOutcome.Added, await repo.AddAsync(bare, 9, "Nine"));

        var noName = New();
        Raw().InsertOne(new BsonDocument { { "_id", (int)noName }, { "Entries", new BsonArray { new BsonDocument { { "Uid", 4L } } } } });
        var e = typed.Find(d => d.Id == noName).Single().Entries.Single();
        Assert.Equal(4UL, e.Uid);
        Assert.Equal(string.Empty, e.Name);                                      // missing Name -> string.Empty
    }

    [DockerFact]
    public async Task FriendPresence_block_filter_returns_exactly_the_owners_whose_list_holds_the_character()
    {
        var (repo, _, ctx) = Open();
        var blocker = New(); var other = New(); var empty = New();
        const uint target = 4242;
        await repo.AddAsync(blocker, target, "T");
        await repo.AddAsync(other, target + 1, "U");
        Raw().InsertOne(new BsonDocument { { "_id", (int)empty }, { "Entries", new BsonArray() } });

        var presence = new FriendPresence(new MongoFriendListRepository(ctx), repo, null!);
        var m = typeof(FriendPresence).GetMethod("OwnersBlockingAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var blockers = await (Task<HashSet<uint>>)m.Invoke(presence, new object[] { new uint[] { blocker, other, empty, New() }, target })!;
        Assert.Equal(new[] { blocker }, blockers.ToArray());                     // suppressed from 23502/23503: only the blocker
        Assert.Empty(await (Task<HashSet<uint>>)m.Invoke(presence, new object[] { Array.Empty<uint>(), target })!);
    }
}
