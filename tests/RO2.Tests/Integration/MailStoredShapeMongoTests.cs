using System.Diagnostics;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using RO2.Mails.Domain;
using RO2.Persistence.Mongo.Persistence;
using RO2.Mails.Infrastructure.Mongo;
using Testcontainers.MongoDb;
using Xunit;
using Xunit.Abstractions;

namespace RO2.Tests.Integration;

/// <summary>
/// A [Fact] that is reported as SKIPPED (never green) when no Docker daemon answers. The Quest/Postgres twins return early on
/// <c>!Available</c>, which reads as a pass; here the skip is visible, and when Docker IS present the fixture must have started
/// (every test asserts it), so a broken container is a failure, not a skip.
/// </summary>
public sealed class DockerFactAttribute : FactAttribute
{
    private static readonly Lazy<bool> DockerUp = new(Probe);

    public DockerFactAttribute()
    {
        if (!DockerUp.Value) Skip = "Docker daemon not reachable: the container-backed (Mongo/Postgres) test did NOT run";
    }

    private static bool Probe()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("docker", "version --format {{.Server.Version}}")
                { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
            return p.WaitForExit(15_000) && p.ExitCode == 0;
        }
        catch { return false; }
    }
}

/// <summary>One ephemeral mongo:7 container shared by <see cref="MongoMailStoredShapeTests"/>.</summary>
public sealed class MongoMailFixture : IAsyncLifetime
{
    private readonly MongoDbContainer _container = new MongoDbBuilder("mongo:7").Build();
    public MongoClient? Client { get; private set; }
    public bool Available => Client is not null;
    public string ConnectionString { get; private set; } = "";
    public string StartupError { get; private set; } = "";

    public async Task InitializeAsync()
    {
        try
        {
            await _container.StartAsync();
            ConnectionString = _container.GetConnectionString();
            Client = new MongoClient(ConnectionString);
        }
        catch (Exception ex)
        {
            Client = null;
            StartupError = ex.GetType().Name + ": " + ex.Message;
        }
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();
}

[CollectionDefinition("MongoMail", DisableParallelization = true)]
public sealed class MongoMailCollection : ICollectionFixture<MongoMailFixture> { }

/// <summary>
/// B9 S1: the Mongo twin of <see cref="PostgresMailTests"/>, against a REAL mongo:7 container. It pins the stored shape of
/// <see cref="MailMessage"/> (field names, BSON types, defaults, the legacy-document load) and every rule of the Mongo
/// <see cref="MailRepository"/> (uid formula, claim CAS, DeleteIfSpent, inbox cap/order, live-inbox bound), so a byte-identical
/// move of the entity or the repository keeps it green. Every value below was observed from the current code, not derived.
/// </summary>
[Collection("MongoMail")]
public sealed class MongoMailStoredShapeTests
{
    private const string Db = "ro2_it_mail";
    private static readonly DateTime Base = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
    private static long _receiverSeq = Random.Shared.Next(1_000_000, 1_000_000_000);

    private readonly MongoMailFixture _fx;
    private readonly ITestOutputHelper _out;
    public MongoMailStoredShapeTests(MongoMailFixture fx, ITestOutputHelper output) { _fx = fx; _out = output; }

    // uint is Int32 up to int.MaxValue and Int64 above (LenientUInt32Serializer); the sequence ids here stay small.
    private static uint NewReceiver() => (uint)Interlocked.Increment(ref _receiverSeq);

    private (MailRepository repo, IMongoCollection<BsonDocument> raw) Open()
    {
        Assert.True(_fx.Available, "Docker is up but the mongo:7 fixture did not start: " + _fx.StartupError);
        var ctx = new MongoDbContext(_fx.ConnectionString, Db);
        return (new MailRepository(ctx), _fx.Client!.GetDatabase(Db).GetCollection<BsonDocument>("mails"));
    }

    private static MailMessage Draft(uint receiver, DateTime? sentAt = null) => new()
    {
        ReceiverCharId = receiver, ReceiverName = "Rcv", SenderCharId = 7, SenderName = "Snd",
        Title = "Hello", Body = "Body", SentAt = sentAt ?? Base,
    };

    private static BsonDocument Raw(IMongoCollection<BsonDocument> raw, MailMessage m)
        => raw.Find(Builders<BsonDocument>.Filter.Eq("_id", (long)m.Id)).Single();

    // ------------------------------------------------------------------------------------------------ proof it ran

    [DockerFact]
    public async Task Ran_against_a_real_mongo7_container()
    {
        Assert.True(_fx.Available, "Docker is up but the mongo:7 fixture did not start: " + _fx.StartupError);
        var admin = _fx.Client!.GetDatabase("admin");
        var info = await admin.RunCommandAsync<BsonDocument>(new BsonDocument("buildInfo", 1));
        var version = info["version"].AsString;
        _out.WriteLine($"MongoMailFixture.Available=True mongod={version} at {_fx.ConnectionString}");
        Assert.StartsWith("7.", version);
    }

    // ------------------------------------------------------------------------------------------------ stored shape

    [DockerFact]
    public async Task Every_field_round_trips_and_the_document_shape_is_pinned()
    {
        var (repo, raw) = Open();
        var rcv = NewReceiver();
        var draft = new MailMessage
        {
            ReceiverCharId = rcv, ReceiverName = "Receiver", SenderCharId = 2_000_000_001u, SenderName = "Sender",
            Title = "Title é中", Body = "Line1\nLine2",
            AttachedItemUid = 0x0123_4567_89AB_CDEFUL, AttachedItemId = 2_000_000_123u, AttachedItemCount = 42,
            AttachedItemRefine = 9, AttachedItemDurability = 250, AttachedItemCardsJson = "[1,2,3]",
            AttachedItemElement = 3, AttachedItemDyeColor = 0x7FAABBCCu, AttachedItemSocketPunchersJson = "{\"a\":1}",
            AttachedItemDyeColor2 = 0x7F000001u, AttachedItemDyeColor3 = 0x7F000002u, AttachedItemFxEnchantIndex = 2_000_000_000u,
            AttachedItemEnchantReinforceId = 65_535, AttachedItemEnchantSeconds = 60_000, AttachedItemBound = true,
            AttachedItemAbilityValue = 0, ItemClaimed = true, Gold = 9_000_000_000L, GoldClaimed = true, IsRead = true,
            SentAt = Base.AddMilliseconds(123).AddTicks(4567), Kind = 11, KindItemId = 2_100_000_000u, AuctionPrice = 5_000_000_500L,
        };

        var added = await repo.AddAsync(draft);
        var doc = Raw(raw, added);

        Assert.Equal(new[]
        {
            "_id", "Uid", "ReceiverCharId", "ReceiverName", "SenderCharId", "SenderName", "Title", "Body",
            "AttachedItemUid", "AttachedItemId", "AttachedItemCount", "AttachedItemRefine", "AttachedItemDurability",
            "AttachedItemCardsJson", "AttachedItemElement", "AttachedItemDyeColor", "AttachedItemSocketPunchersJson",
            "AttachedItemDyeColor2", "AttachedItemDyeColor3", "AttachedItemFxEnchantIndex", "AttachedItemEnchantReinforceId",
            "AttachedItemEnchantSeconds", "AttachedItemBound", "AttachedItemAbilityValue", "ItemClaimed", "Gold", "GoldClaimed",
            "IsRead", "SentAt", "Kind", "KindItemId", "AuctionPrice",
        }, doc.Names.ToArray());                                    // PascalCase, Id -> _id, declaration order, no HasUnclaimedAttachment

        Assert.Equal((long)added.Id, doc["_id"].ToInt64());         // _id is the ulong Id ...
        Assert.Equal((long)added.Uid, doc["Uid"].ToInt64());        // ... Uid is a second field, not the key
        Assert.Equal(BsonType.Int64, doc["_id"].BsonType);
        Assert.Equal(BsonType.Int64, doc["Uid"].BsonType);
        Assert.Equal(BsonType.Int64, doc["AttachedItemUid"].BsonType);
        Assert.Equal(BsonType.Int32, doc["ReceiverCharId"].BsonType);   // uint -> Int32
        Assert.Equal(BsonType.Int32, doc["AttachedItemId"].BsonType);
        Assert.Equal(BsonType.Int32, doc["AttachedItemCount"].BsonType);
        Assert.Equal(BsonType.Int32, doc["AttachedItemRefine"].BsonType);      // byte -> Int32
        Assert.Equal(BsonType.Int32, doc["AttachedItemDurability"].BsonType);  // short -> Int32
        Assert.Equal(BsonType.Int32, doc["AttachedItemEnchantReinforceId"].BsonType);
        Assert.Equal(BsonType.Int32, doc["Kind"].BsonType);
        Assert.Equal(BsonType.Int64, doc["Gold"].BsonType);
        Assert.Equal(BsonType.Int64, doc["AuctionPrice"].BsonType);
        Assert.Equal(BsonType.Boolean, doc["ItemClaimed"].BsonType);
        Assert.Equal(BsonType.DateTime, doc["SentAt"].BsonType);
        Assert.Equal(BsonType.String, doc["Title"].BsonType);
        Assert.Equal(0, doc["AttachedItemAbilityValue"].ToInt32());     // a stored 0 is a real value, not null
        Assert.Equal(BsonType.Int32, doc["AttachedItemAbilityValue"].BsonType);

        var got = (await repo.GetByUidAsync(rcv, added.Uid))!;
        Assert.Equal(added.Id, got.Id);
        Assert.Equal(added.Uid, got.Uid);
        Assert.Equal("Receiver", got.ReceiverName);
        Assert.Equal(2_000_000_001u, got.SenderCharId);
        Assert.Equal("Sender", got.SenderName);
        Assert.Equal("Title é中", got.Title);
        Assert.Equal("Line1\nLine2", got.Body);
        Assert.Equal(0x0123_4567_89AB_CDEFUL, got.AttachedItemUid);
        Assert.Equal(2_000_000_123u, got.AttachedItemId);
        Assert.Equal(42, got.AttachedItemCount);
        Assert.Equal((byte)9, got.AttachedItemRefine);
        Assert.Equal((short)250, got.AttachedItemDurability);
        Assert.Equal("[1,2,3]", got.AttachedItemCardsJson);
        Assert.Equal((byte)3, got.AttachedItemElement);
        Assert.Equal(0x7FAABBCCu, got.AttachedItemDyeColor);
        Assert.Equal("{\"a\":1}", got.AttachedItemSocketPunchersJson);
        Assert.Equal(0x7F000001u, got.AttachedItemDyeColor2);
        Assert.Equal(0x7F000002u, got.AttachedItemDyeColor3);
        Assert.Equal(2_000_000_000u, got.AttachedItemFxEnchantIndex);
        Assert.Equal((ushort)65_535, got.AttachedItemEnchantReinforceId);
        Assert.Equal((ushort)60_000, got.AttachedItemEnchantSeconds);
        Assert.True(got.AttachedItemBound);
        Assert.Equal(0u, got.AttachedItemAbilityValue);
        Assert.True(got.ItemClaimed);
        Assert.Equal(9_000_000_000L, got.Gold);
        Assert.True(got.GoldClaimed);
        Assert.True(got.IsRead);
        Assert.Equal((byte)11, got.Kind);
        Assert.Equal(2_100_000_000u, got.KindItemId);
        Assert.Equal(5_000_000_500L, got.AuctionPrice);
        Assert.True(got.HasUnclaimedAttachment is false);
    }

    [DockerFact]
    public async Task Defaults_are_stored_explicitly_null_stays_null_and_strings_are_empty_not_null()
    {
        var (repo, raw) = Open();
        var rcv = NewReceiver();
        var added = await repo.AddAsync(new MailMessage { ReceiverCharId = rcv });   // every default
        var doc = Raw(raw, added);

        Assert.Equal(-1, doc["AttachedItemDurability"].ToInt32());       // the -1 "unknown durability" default is stored
        Assert.Equal(BsonType.Null, doc["AttachedItemCardsJson"].BsonType);          // null string stays BSON null
        Assert.Equal(BsonType.Null, doc["AttachedItemSocketPunchersJson"].BsonType);
        Assert.Equal(BsonType.Null, doc["AttachedItemAbilityValue"].BsonType);       // null (template value) != 0
        foreach (var f in new[] { "ReceiverName", "SenderName", "Title", "Body" })
        {
            Assert.Equal(BsonType.String, doc[f].BsonType);
            Assert.Equal(string.Empty, doc[f].AsString);                              // empty, never null
        }
        foreach (var f in new[] { "AttachedItemId", "AttachedItemCount", "AttachedItemRefine", "AttachedItemElement", "Kind",
                     "KindItemId", "AttachedItemDyeColor", "AttachedItemDyeColor2", "AttachedItemDyeColor3",
                     "AttachedItemFxEnchantIndex", "AttachedItemEnchantReinforceId", "AttachedItemEnchantSeconds", "SenderCharId" })
            Assert.Equal(0, doc[f].ToInt32());
        foreach (var f in new[] { "Gold", "AuctionPrice", "AttachedItemUid" }) Assert.Equal(0L, doc[f].ToInt64());
        foreach (var f in new[] { "AttachedItemBound", "ItemClaimed", "GoldClaimed", "IsRead" }) Assert.False(doc[f].AsBoolean);

        var got = (await repo.GetByUidAsync(rcv, added.Uid))!;
        Assert.Null(got.AttachedItemCardsJson);
        Assert.Null(got.AttachedItemSocketPunchersJson);
        Assert.Null(got.AttachedItemAbilityValue);
        Assert.Equal((short)-1, got.AttachedItemDurability);
        Assert.Equal(string.Empty, got.Title);
    }

    [DockerFact]
    public async Task SentAt_is_stored_at_millisecond_precision_and_read_back_as_utc()
    {
        var (repo, raw) = Open();
        var rcv = NewReceiver();
        var sent = Base.AddMilliseconds(123).AddTicks(4567);                 // 123.4567 ms
        var added = await repo.AddAsync(Draft(rcv, sent));

        var doc = Raw(raw, added);
        Assert.Equal(new DateTimeOffset(Base.AddMilliseconds(123)).ToUnixTimeMilliseconds(), doc["SentAt"].AsBsonDateTime.MillisecondsSinceEpoch);

        var got = (await repo.GetByUidAsync(rcv, added.Uid))!;
        Assert.Equal(Base.AddMilliseconds(123), got.SentAt);                 // the 0.4567 ms is truncated
        Assert.Equal(DateTimeKind.Utc, got.SentAt.Kind);
        Assert.Equal(DateTimeKind.Utc, (await repo.GetInboxAsync(rcv)).Single().SentAt.Kind);
    }

    [DockerFact]
    public async Task A_legacy_document_without_the_newer_fields_loads_with_the_neutral_defaults()
    {
        var (repo, raw) = Open();
        var rcv = NewReceiver();
        const ulong id = 777_000_001;
        var uid = (0x0E4B4B8BUL << 32) | id;
        await raw.InsertOneAsync(new BsonDocument   // the pre-Kind/Element/Dye/AbilityValue/AuctionPrice shape, plus a field no class knows
        {
            { "_id", (long)id }, { "Uid", (long)uid }, { "ReceiverCharId", (int)rcv }, { "ReceiverName", "R" },
            { "SenderCharId", 5 }, { "SenderName", "S" }, { "Title", "T" }, { "Body", "B" },
            { "AttachedItemUid", 0L }, { "AttachedItemId", 501 }, { "AttachedItemCount", 3 }, { "Gold", 100L },
            { "ItemClaimed", false }, { "GoldClaimed", false }, { "IsRead", false }, { "SentAt", Base },
            { "SomeFutureField", "ignored" },
        });

        var got = (await repo.GetByUidAsync(rcv, uid))!;                     // IgnoreExtraElements: no throw on SomeFutureField
        Assert.Equal(id, got.Id);
        Assert.Equal(501u, got.AttachedItemId);
        Assert.Equal((byte)0, got.Kind);
        Assert.Equal(0u, got.KindItemId);
        Assert.Equal(0L, got.AuctionPrice);
        Assert.Equal((byte)0, got.AttachedItemElement);
        Assert.Equal(0u, got.AttachedItemDyeColor);
        Assert.Null(got.AttachedItemSocketPunchersJson);
        Assert.Null(got.AttachedItemAbilityValue);                           // null = the template's Abili_val
        Assert.Equal((short)-1, got.AttachedItemDurability);                 // the property initializer, as the field is absent
        Assert.False(got.AttachedItemBound);
        Assert.Null(got.AttachedItemCardsJson);
        Assert.True(got.HasUnclaimedAttachment);
    }

    [DockerFact]
    public async Task Uints_above_int_max_insert_as_Int64_and_read_back()
    {
        var (repo, raw) = Open();
        var before = await repo.AddAsync(Draft(NewReceiver()));

        // The uint ReceiverCharId (here 3e9) is stored as Int64, and no sequence id is burnt.
        var big = await repo.AddAsync(Draft(3_000_000_000u));
        Assert.Equal(before.Id + 1, big.Id);
        Assert.Equal(BsonType.Int64, raw.Find(Builders<BsonDocument>.Filter.Eq("Uid", (long)((0x0E4B4B8BUL << 32) | big.Id))).Single()["ReceiverCharId"].BsonType);
        Assert.Equal(3_000_000_000u, big.ReceiverCharId);
    }

    // ------------------------------------------------------------------------------------------------ ids and uids

    [DockerFact]
    public async Task Uid_is_0x0E4B4B8B_shifted_32_or_the_low_dword_of_a_sequence_id_stored_in_mail_counters()
    {
        var (repo, _) = Open();
        var rcv = NewReceiver();
        var a = await repo.AddAsync(Draft(rcv));
        var b = await repo.AddAsync(Draft(rcv));

        Assert.True(a.Id > 0 && b.Id == a.Id + 1);                            // one shared, strictly increasing sequence
        Assert.Equal((0x0E4B4B8BUL << 32) | (uint)a.Id, a.Uid);
        Assert.Equal((0x0E4B4B8BUL << 32) | (uint)b.Id, b.Uid);
        Assert.Equal(0x0E4B4B8BUL, a.Uid >> 32);

        var counter = _fx.Client!.GetDatabase(Db).GetCollection<BsonDocument>("mail_counters")
            .Find(Builders<BsonDocument>.Filter.Eq("_id", "mailId")).Single();
        Assert.Equal(new[] { "_id", "seq" }, counter.Names.ToArray());
        Assert.Equal(BsonType.Int64, counter["seq"].BsonType);
        Assert.True(counter["seq"].ToInt64() >= (long)b.Id);                  // other tests of this collection draw from it too
    }

    [DockerFact]
    public async Task Concurrent_adds_get_unique_ids_and_uids()
    {
        var (repo, _) = Open();
        var rcv = NewReceiver();
        var added = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => repo.AddAsync(Draft(rcv))));

        Assert.Equal(20, added.Select(m => m.Id).Distinct().Count());
        Assert.Equal(20, added.Select(m => m.Uid).Distinct().Count());
        Assert.Equal(20, (await repo.GetInboxAsync(rcv)).Count);
    }

    [DockerFact]
    public async Task The_two_mail_indexes_are_a_receiver_newest_first_compound_and_a_unique_uid()
    {
        Assert.True(_fx.Available, _fx.StartupError);
        // The host builds the indexes through EnsureIndexesAsync (never the constructor).
        var db = "ro2_it_mail_idx";
        Assert.True(await new MailRepository(new MongoDbContext(_fx.ConnectionString, db)).EnsureIndexesAsync());

        var idx = (await _fx.Client!.GetDatabase(db).GetCollection<BsonDocument>("mails").Indexes.ListAsync()).ToList();
        var byName = idx.ToDictionary(d => d["name"].AsString);
        Assert.Equal(new[] { "ReceiverCharId_1_SentAt_-1", "Uid_1", "_id_" }.Order(), byName.Keys.Order());
        Assert.False(byName["ReceiverCharId_1_SentAt_-1"].Contains("unique"));
        Assert.True(byName["Uid_1"]["unique"].AsBoolean);
    }

    // ------------------------------------------------------------------------------------------------ reads

    [DockerFact]
    public async Task Inbox_is_newest_first_capped_at_200_and_only_the_receivers()
    {
        var (repo, _) = Open();
        var rcv = NewReceiver();
        var other = NewReceiver();
        var docs = Enumerable.Range(0, 205).Select(i => Draft(rcv, Base.AddMinutes(i))).Reverse().ToList();   // inserted newest first
        foreach (var d in docs) await repo.AddAsync(d);
        await repo.AddAsync(Draft(other, Base.AddDays(9)));

        var inbox = await repo.GetInboxAsync(rcv);
        Assert.Equal(200, inbox.Count);
        Assert.All(inbox, m => Assert.Equal(rcv, m.ReceiverCharId));
        Assert.Equal(Base.AddMinutes(204), inbox[0].SentAt);
        Assert.Equal(Base.AddMinutes(5), inbox[199].SentAt);                  // the 5 oldest fall off the cap
        Assert.Equal(inbox.Select(m => m.SentAt).OrderByDescending(t => t), inbox.Select(m => m.SentAt));
        Assert.Empty(await repo.GetInboxAsync(NewReceiver()));
    }

    [DockerFact]
    public async Task GetByUid_scopes_to_the_receiver_and_misses_are_null_and_unread_counts_are_per_receiver()
    {
        var (repo, _) = Open();
        var rcv = NewReceiver();
        var a = await repo.AddAsync(Draft(rcv));
        await repo.AddAsync(Draft(rcv));
        await repo.AddAsync(Draft(NewReceiver()));

        Assert.NotNull(await repo.GetByUidAsync(rcv, a.Uid));
        Assert.Null(await repo.GetByUidAsync(NewReceiver(), a.Uid));
        Assert.Null(await repo.GetByUidAsync(rcv, a.Uid + 0x1_0000_0000UL));

        Assert.Equal(2L, await repo.CountUnreadAsync(rcv));
        a.IsRead = true;
        await repo.ReplaceAsync(a);
        Assert.Equal(1L, await repo.CountUnreadAsync(rcv));
    }

    [DockerFact]
    public async Task CountLiveInbox_is_inclusive_of_notBefore_counts_read_and_claimed_and_expired_mail_is_never_deleted()
    {
        var (repo, _) = Open();
        var rcv = NewReceiver();
        var expired = await repo.AddAsync(Draft(rcv, Base.AddDays(-31)));
        await repo.AddAsync(Draft(rcv, Base.AddMilliseconds(-1)));
        await repo.AddAsync(Draft(rcv, Base));
        var fresh = Draft(rcv, Base.AddDays(1));
        fresh.IsRead = true; fresh.ItemClaimed = true; fresh.GoldClaimed = true;
        await repo.AddAsync(fresh);
        await repo.AddAsync(Draft(NewReceiver(), Base.AddDays(1)));

        Assert.Equal(2L, await repo.CountLiveInboxAsync(rcv, Base));          // == notBefore counts; -1 ms does not
        Assert.Equal(1L, await repo.CountLiveInboxAsync(rcv, Base.AddMilliseconds(1)));
        Assert.Equal(4L, await repo.CountLiveInboxAsync(rcv, Base.AddDays(-365)));
        Assert.Equal(0L, await repo.CountLiveInboxAsync(rcv, Base.AddDays(2)));
        Assert.Equal(0L, await repo.CountLiveInboxAsync(NewReceiver(), Base.AddDays(-365)));
        Assert.Equal(3L, await repo.CountLiveInboxAsync(rcv, Base.AddDays(-30)));   // the 31-day-old mail drops out of the live count ...

        Assert.Equal(4, (await repo.GetInboxAsync(rcv)).Count);               // ... but never out of the list
        Assert.NotNull(await repo.GetByUidAsync(rcv, expired.Uid));           // ... nor out of get-by-uid
    }

    // ------------------------------------------------------------------------------------------------ replace / delete

    [DockerFact]
    public async Task Replace_overwrites_the_whole_document_by_id_and_an_unknown_id_is_a_no_op()
    {
        var (repo, raw) = Open();
        var rcv = NewReceiver();
        var draft = Draft(rcv);
        draft.AttachedItemId = 501; draft.AttachedItemCount = 5; draft.AttachedItemCardsJson = "[9]";
        draft.AttachedItemAbilityValue = 7; draft.Gold = 100;
        var added = await repo.AddAsync(draft);

        var edit = (await repo.GetByUidAsync(rcv, added.Uid))!;
        edit.IsRead = true; edit.ItemClaimed = true; edit.GoldClaimed = true; edit.Title = "Edited";
        edit.AttachedItemCardsJson = null; edit.AttachedItemAbilityValue = null; edit.Gold = 0;
        await repo.ReplaceAsync(edit);

        var got = (await repo.GetByUidAsync(rcv, added.Uid))!;
        Assert.Equal("Edited", got.Title);
        Assert.True(got.IsRead && got.ItemClaimed && got.GoldClaimed);
        Assert.Null(got.AttachedItemCardsJson);
        Assert.Null(got.AttachedItemAbilityValue);
        Assert.Equal(0L, got.Gold);
        Assert.Equal(501u, got.AttachedItemId);
        Assert.Single(await repo.GetInboxAsync(rcv));

        var ghost = Draft(rcv);
        ghost.Id = 9_000_000_000_000UL + (ulong)Random.Shared.Next(1, 1_000_000);
        ghost.Uid = ghost.Id;
        await repo.ReplaceAsync(ghost);                                       // no upsert
        Assert.Single(await repo.GetInboxAsync(rcv));
        Assert.Equal(0, raw.CountDocuments(Builders<BsonDocument>.Filter.Eq("_id", (long)ghost.Id)));
    }

    [DockerFact]
    public async Task Delete_removes_once_is_receiver_scoped_and_a_race_has_exactly_one_winner()
    {
        var (repo, _) = Open();
        var rcv = NewReceiver();
        var a = await repo.AddAsync(Draft(rcv));
        Assert.False(await repo.DeleteAsync(NewReceiver(), a.Uid));
        Assert.NotNull(await repo.GetByUidAsync(rcv, a.Uid));
        Assert.True(await repo.DeleteAsync(rcv, a.Uid));
        Assert.False(await repo.DeleteAsync(rcv, a.Uid));
        Assert.Null(await repo.GetByUidAsync(rcv, a.Uid));

        var b = await repo.AddAsync(Draft(rcv));
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => repo.DeleteAsync(rcv, b.Uid)));
        Assert.Equal(1, results.Count(r => r));
    }

    // ------------------------------------------------------------------------------------------------ claim CAS

    [Fact]
    public void The_claim_filters_render_to_receiver_uid_unclaimed_and_attached()
    {
        static BsonDocument R(FilterDefinition<MailMessage> f) => f.Render(new RenderArgs<MailMessage>(
            BsonSerializer.SerializerRegistry.GetSerializer<MailMessage>(), BsonSerializer.SerializerRegistry));

        Assert.Equal("{ \"ReceiverCharId\" : 5, \"Uid\" : 77, \"ItemClaimed\" : false, \"AttachedItemId\" : { \"$ne\" : 0 } }",
            R(MailRepository.BuildClaimItemFilter(5, 77)).ToString());
        Assert.Equal("{ \"ReceiverCharId\" : 5, \"Uid\" : 77, \"GoldClaimed\" : false, \"Gold\" : { \"$gt\" : 0 } }",
            R(MailRepository.BuildClaimGoldFilter(5, 77)).ToString());
    }

    [DockerFact]
    public async Task Claim_item_and_gold_each_have_exactly_one_winner_under_a_race_and_flip_only_their_flag()
    {
        var (repo, raw) = Open();
        var rcv = NewReceiver();
        var both = await repo.AddAsync(new MailMessage { ReceiverCharId = rcv, AttachedItemId = 501, AttachedItemCount = 2, Gold = 900 });

        var items = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => repo.TryClaimItemAsync(rcv, both.Uid)));
        Assert.Equal(1, items.Count(r => r is not null));
        var won = items.Single(r => r is not null)!;
        Assert.True(won.ItemClaimed);                                         // the winner is returned post-flip
        Assert.False(won.GoldClaimed);                                        // only the item flag moved
        Assert.Equal((501u, 2, 900L), (won.AttachedItemId, won.AttachedItemCount, won.Gold));   // with the attachment snapshot
        var mid = Raw(raw, both);
        Assert.True(mid["ItemClaimed"].AsBoolean);
        Assert.False(mid["GoldClaimed"].AsBoolean);

        var golds = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => repo.TryClaimGoldAsync(rcv, both.Uid)));
        Assert.Equal(1, golds.Count(r => r is not null));
        Assert.True(golds.Single(r => r is not null)!.GoldClaimed);
        var stored = (await repo.GetByUidAsync(rcv, both.Uid))!;
        Assert.True(stored.ItemClaimed && stored.GoldClaimed);
        Assert.Null(await repo.TryClaimItemAsync(rcv, both.Uid));             // spent: nothing more to claim
        Assert.Null(await repo.TryClaimGoldAsync(rcv, both.Uid));
    }

    [DockerFact]
    public async Task Claim_returns_null_for_the_wrong_receiver_an_unknown_uid_and_nothing_attached()
    {
        var (repo, _) = Open();
        var rcv = NewReceiver();
        var both = await repo.AddAsync(new MailMessage { ReceiverCharId = rcv, AttachedItemId = 501, Gold = 900 });
        var empty = await repo.AddAsync(new MailMessage { ReceiverCharId = rcv });
        var negative = await repo.AddAsync(new MailMessage { ReceiverCharId = rcv, Gold = -5 });

        Assert.Null(await repo.TryClaimItemAsync(NewReceiver(), both.Uid));
        Assert.Null(await repo.TryClaimGoldAsync(NewReceiver(), both.Uid));
        Assert.Null(await repo.TryClaimItemAsync(rcv, 0xDEAD_BEEFUL));
        Assert.Null(await repo.TryClaimItemAsync(rcv, empty.Uid));            // AttachedItemId == 0
        Assert.Null(await repo.TryClaimGoldAsync(rcv, empty.Uid));            // Gold == 0
        Assert.Null(await repo.TryClaimGoldAsync(rcv, negative.Uid));         // Gold <= 0
        var untouched = (await repo.GetByUidAsync(rcv, both.Uid))!;
        Assert.False(untouched.ItemClaimed || untouched.GoldClaimed);         // a refused claim flips nothing
    }

    // ------------------------------------------------------------------------------------------------ DeleteIfSpent

    // item (id != 0?, claimed?)  gold (amount, claimed?)  expected deleted. Spent = (no item OR item claimed) AND (gold <= 0 OR gold claimed).
    [DockerTheory]
    [InlineData(0u, false, 0L, false, true)]       // nothing attached
    [InlineData(501u, false, 0L, false, false)]    // unclaimed item
    [InlineData(501u, true, 0L, false, true)]      // claimed item
    [InlineData(0u, false, 5000L, false, false)]   // unclaimed zeny
    [InlineData(0u, false, 5000L, true, true)]     // claimed zeny
    [InlineData(0u, false, -10L, false, true)]     // non-positive gold is nothing pending
    [InlineData(501u, true, 5000L, false, false)]  // item spent, zeny not
    [InlineData(501u, false, 5000L, true, false)]  // zeny spent, item not
    [InlineData(501u, true, 5000L, true, true)]    // both spent
    [InlineData(0u, true, 0L, true, true)]         // flags set with nothing attached
    public async Task DeleteIfSpent_matrix(uint itemId, bool itemClaimed, long gold, bool goldClaimed, bool expectDeleted)
    {
        var (repo, _) = Open();
        var rcv = NewReceiver();
        var added = await repo.AddAsync(new MailMessage
            { ReceiverCharId = rcv, AttachedItemId = itemId, ItemClaimed = itemClaimed, Gold = gold, GoldClaimed = goldClaimed });

        Assert.Equal(expectDeleted, await repo.DeleteIfSpentAsync(rcv, added.Uid));
        Assert.Equal(!expectDeleted, await repo.GetByUidAsync(rcv, added.Uid) is not null);
    }

    [DockerFact]
    public async Task DeleteIfSpent_is_receiver_scoped_unknown_uid_false_sees_a_later_claim_and_a_race_has_one_winner()
    {
        var (repo, _) = Open();
        var rcv = NewReceiver();
        var spent = await repo.AddAsync(Draft(rcv));
        Assert.False(await repo.DeleteIfSpentAsync(NewReceiver(), spent.Uid));
        Assert.NotNull(await repo.GetByUidAsync(rcv, spent.Uid));
        Assert.False(await repo.DeleteIfSpentAsync(rcv, 0xDEAD_BEEFUL));

        var pending = Draft(rcv);
        pending.AttachedItemId = 501;
        var p = await repo.AddAsync(pending);
        Assert.False(await repo.DeleteIfSpentAsync(rcv, p.Uid));
        Assert.NotNull(await repo.TryClaimItemAsync(rcv, p.Uid));              // the claim flips the flag ...
        Assert.True(await repo.DeleteIfSpentAsync(rcv, p.Uid));                // ... and the spent mail now goes

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => repo.DeleteIfSpentAsync(rcv, spent.Uid)));
        Assert.Equal(1, results.Count(r => r));
    }
}

/// <summary>[Theory] twin of <see cref="DockerFactAttribute"/>.</summary>
public sealed class DockerTheoryAttribute : TheoryAttribute
{
    public DockerTheoryAttribute()
    {
        var probe = new DockerFactAttribute();
        if (probe.Skip is not null) Skip = probe.Skip;
    }
}
