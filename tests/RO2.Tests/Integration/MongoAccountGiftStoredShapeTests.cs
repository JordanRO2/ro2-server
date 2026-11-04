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
/// B12 S1: the Mongo twin of <see cref="PostgresAccountGiftTests"/>, against a REAL mongo:7 container (the <see cref="MongoMailFixture"/>
/// of the Mail twin, its own database). It pins the stored shape of the <c>account_gifts</c> collection (the repository-private
/// <c>AccountGiftDocument</c> holding <see cref="AccountGift"/> elements, plus the attendance fields) and every rule of the Mongo
/// <see cref="AccountGiftRepository"/> (the legacy box without attendance, the attendance compare-and-set and its DuplicateKey path,
/// the expiry pull), so a byte-identical move of the entity, the rules or the repository keeps it green. Every value below was
/// observed from the current code, not derived. Every test reports SKIPPED without Docker and asserts the fixture started when Docker
/// is up.
/// </summary>
[Collection("MongoMail")]
public sealed class MongoAccountGiftStoredShapeTests
{
    private const string Db = "ro2_it_account_gifts";
    private const string Coll = "account_gifts";
    private static readonly DateTime NowLocal = new(2026, 6, 15, 12, 0, 0, DateTimeKind.Local);
    private static long _accSeq = Random.Shared.Next(1_000_000, 1_000_000_000);

    private readonly MongoMailFixture _fx;
    private readonly ITestOutputHelper _out;
    public MongoAccountGiftStoredShapeTests(MongoMailFixture fx, ITestOutputHelper output) { _fx = fx; _out = output; }

    // uint is Int32 up to int.MaxValue and Int64 above (LenientUInt32Serializer); the sequence ids here stay small.
    private static uint NewAccount() => (uint)Interlocked.Increment(ref _accSeq);

    private static AccountGift Gift(int daysAgo, uint itemId = 501, ushort count = 3, byte source = 1) => new()
    {
        Guid = Convert.ToHexString(System.Guid.NewGuid().ToByteArray()),   // the production form: 32 UPPERCASE hex digits
        ItemId = itemId,
        Count = count,
        ReceivedUtc = NowLocal.AddDays(-daysAgo).ToUniversalTime(),
        Source = source,
    };

    private AccountGiftRepository Repo(string db = Db)
    {
        Assert.True(_fx.Available, "Docker is up but the mongo:7 fixture did not start: " + _fx.StartupError);
        return new AccountGiftRepository(new MongoDbContext(_fx.ConnectionString, db));
    }

    private IMongoCollection<BsonDocument> Raw(string db = Db)
        => _fx.Client!.GetDatabase(db).GetCollection<BsonDocument>(Coll);

    private BsonDocument One(uint id, string db = Db)
        => Raw(db).Find(new BsonDocument("_id", (int)id)).Single();

    private long Count(uint id, string db = Db) => Raw(db).CountDocuments(new BsonDocument("_id", (int)id));

    private static BsonDocument RawGift(AccountGift g) => new()
    {
        { "Guid", g.Guid }, { "ItemId", (int)g.ItemId }, { "Count", (int)g.Count },
        { "ReceivedUtc", new BsonDateTime(g.ReceivedUtc) }, { "Source", (int)g.Source },
    };

    /// <summary>Inserts a raw document; the attendance fields are written only when <paramref name="attendance"/> is given (else a legacy box).</summary>
    private void Seed(uint id, IEnumerable<AccountGift> gifts, DateTime updatedAt, (int Days, int Last, int Broken)? attendance = null)
    {
        var doc = new BsonDocument { { "_id", (int)id }, { "Gifts", new BsonArray(gifts.Select(RawGift)) }, { "UpdatedAt", new BsonDateTime(updatedAt) } };
        if (attendance is { } a)
        {
            doc["AttendedDays"] = a.Days;
            doc["LastAttendedDay"] = a.Last;
            doc["AttendanceStreakBrokenAfter"] = a.Broken;
        }
        Raw().InsertOne(doc, new InsertOneOptions { BypassDocumentValidation = true });
    }

    // ------------------------------------------------------------------------------------------------ proof it ran

    [DockerFact]
    public async Task Ran_against_a_real_mongo7_container()
    {
        Assert.True(_fx.Available, "Docker is up but the mongo:7 fixture did not start: " + _fx.StartupError);
        var info = await _fx.Client!.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument("buildInfo", 1));
        var version = info["version"].AsString;
        _out.WriteLine($"MongoAccountGiftStoredShapeTests: MongoMailFixture.Available=True mongod={version} at {_fx.ConnectionString}");
        Assert.StartsWith("7.", version);
    }

    // ------------------------------------------------------------------------------------------------ stored shape

    [DockerFact]
    public async Task A_filed_gift_is_pushed_into_Gifts_with_the_pinned_shape_and_no_attendance_fields()
    {
        var repo = Repo();
        var account = NewAccount();
        var gift = Gift(0, itemId: 607, count: ushort.MaxValue, source: 4);
        var before = DateTime.UtcNow;

        Assert.Equal(1, await repo.FileAsync(account, new[] { gift }, NowLocal));
        var after = DateTime.UtcNow;

        var raw = One(account);
        Assert.Equal(new[] { "_id", "Gifts", "UpdatedAt" }, raw.Names.ToArray());   // an upsert by push + set: no attendance field is written
        Assert.Equal(BsonType.Int32, raw["_id"].BsonType);                          // uint account id -> Int32
        Assert.Equal((int)account, raw["_id"].AsInt32);
        Assert.Equal(BsonType.Array, raw["Gifts"].BsonType);
        var stored = Assert.Single(raw["Gifts"].AsBsonArray).AsBsonDocument;
        Assert.Equal(new[] { "Guid", "ItemId", "Count", "ReceivedUtc", "Source" }, stored.Names.ToArray());   // element field order
        Assert.Equal(BsonType.String, stored["Guid"].BsonType);
        Assert.Equal(gift.Guid, stored["Guid"].AsString);
        Assert.Matches("^[0-9A-F]{32}$", stored["Guid"].AsString);                  // 32 UPPERCASE hex digits
        Assert.Equal(BsonType.Int32, stored["ItemId"].BsonType);                    // uint -> Int32
        Assert.Equal(607, stored["ItemId"].AsInt32);
        Assert.Equal(BsonType.Int32, stored["Count"].BsonType);                     // ushort -> Int32
        Assert.Equal(ushort.MaxValue, stored["Count"].AsInt32);
        Assert.Equal(BsonType.DateTime, stored["ReceivedUtc"].BsonType);            // UTC date, ms resolution
        Assert.Equal(gift.ReceivedUtc, stored["ReceivedUtc"].ToUniversalTime());
        Assert.Equal(BsonType.Int32, stored["Source"].BsonType);                    // byte -> Int32
        Assert.Equal(4, stored["Source"].AsInt32);

        Assert.Equal(BsonType.DateTime, raw["UpdatedAt"].BsonType);
        var stamp = raw["UpdatedAt"].ToUniversalTime();
        Assert.InRange(stamp, new DateTime(before.Ticks - before.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc), after);

        Assert.DoesNotContain("\"_t\"", raw.ToJson());                              // no type discriminator anywhere (document or element)

        // The repository read gives the same gift back, UTC kind.
        var live = Assert.Single(await repo.GetLiveAsync(account, NowLocal));
        Assert.Equal(gift.Guid, live.Guid);
        Assert.Equal(607u, live.ItemId);
        Assert.Equal(ushort.MaxValue, live.Count);
        Assert.Equal((byte)4, live.Source);
        Assert.Equal(DateTimeKind.Utc, live.ReceivedUtc.Kind);
        Assert.Equal(gift.ReceivedUtc, live.ReceivedUtc);
        Assert.Equal(1, Count(account));                                            // one document per account
    }

    [DockerFact]
    public async Task Filing_appends_and_an_empty_list_stores_nothing()
    {
        var repo = Repo();
        var account = NewAccount();

        Assert.Equal(0, await repo.FileAsync(account, Array.Empty<AccountGift>(), NowLocal));
        Assert.Equal(0, Count(account));                                            // nothing filed: not even an upsert
        Assert.Empty(await repo.GetLiveAsync(account, NowLocal));
        Assert.Equal(0, Count(account));                                            // and a read upserts nothing

        Assert.Equal(2, await repo.FileAsync(account, new[] { Gift(0), Gift(1) }, NowLocal));
        Assert.Equal(3, await repo.FileAsync(account, new[] { Gift(2) }, NowLocal));
        Assert.Equal(3, One(account)["Gifts"].AsBsonArray.Count);
        Assert.Equal(1, Count(account));
    }

    [DockerFact]
    public async Task A_legacy_box_without_the_attendance_fields_reads_as_never_attended_and_unknown_accounts_too()
    {
        var repo = Repo();
        var account = NewAccount();
        var gift = Gift(1);
        Seed(account, new[] { gift }, new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc));

        Assert.Equal(new AccountAttendance(0, 0, 0), await repo.GetAttendanceAsync(account));
        Assert.Equal(gift.Guid, Assert.Single(await repo.GetLiveAsync(account, NowLocal)).Guid);   // the box itself still loads

        // Filing into it leaves it a legacy box: still no attendance field, still (0, 0, 0).
        Assert.Equal(2, await repo.FileAsync(account, new[] { Gift(0) }, NowLocal));
        Assert.Equal(new[] { "_id", "Gifts", "UpdatedAt" }, One(account).Names.ToArray());
        Assert.Equal(new AccountAttendance(0, 0, 0), await repo.GetAttendanceAsync(account));

        var absent = NewAccount();
        Assert.Equal(new AccountAttendance(0, 0, 0), await repo.GetAttendanceAsync(absent));
        Assert.Equal(0, Count(absent));
    }

    // ------------------------------------------------------------------------------------------------ the attendance CAS

    [DockerFact]
    public async Task RecordAttendance_first_day_upserts_the_box_with_the_account_id_and_pushes_the_reward()
    {
        var repo = Repo();
        var account = NewAccount();
        var reward = Gift(0, itemId: 607, count: 5, source: 4);

        Assert.True(await repo.TryRecordAttendanceAsync(account, 0, 740_000, 1, 0, reward));

        var raw = One(account);
        Assert.Equal(new[] { "_id", "AttendanceStreakBrokenAfter", "AttendedDays", "Gifts", "LastAttendedDay", "UpdatedAt" }, raw.Names.ToArray());   // an upsert by $set/$push: mongod creates the fields in alphabetical order
        Assert.Equal(BsonType.Int32, raw["_id"].BsonType);
        Assert.Equal((int)account, raw["_id"].AsInt32);                             // the upsert creates _id = (int)accountId
        Assert.Equal(BsonType.Int32, raw["AttendedDays"].BsonType);
        Assert.Equal(1, raw["AttendedDays"].AsInt32);
        Assert.Equal(BsonType.Int32, raw["LastAttendedDay"].BsonType);
        Assert.Equal(740_000, raw["LastAttendedDay"].AsInt32);
        Assert.Equal(BsonType.Int32, raw["AttendanceStreakBrokenAfter"].BsonType);
        Assert.Equal(0, raw["AttendanceStreakBrokenAfter"].AsInt32);
        Assert.Equal(BsonType.DateTime, raw["UpdatedAt"].BsonType);
        var stored = Assert.Single(raw["Gifts"].AsBsonArray).AsBsonDocument;
        Assert.Equal(reward.Guid, stored["Guid"].AsString);
        Assert.Equal(607, stored["ItemId"].AsInt32);
        Assert.Equal(5, stored["Count"].AsInt32);
        Assert.Equal(4, stored["Source"].AsInt32);
        Assert.DoesNotContain("\"_t\"", raw.ToJson());

        Assert.Equal(new AccountAttendance(1, 740_000, 0), await repo.GetAttendanceAsync(account));
        Assert.Equal(reward.Guid, Assert.Single(await repo.GetLiveAsync(account, NowLocal)).Guid);
    }

    [DockerFact]
    public async Task RecordAttendance_first_day_without_a_reward_creates_a_box_with_no_Gifts_field()
    {
        var repo = Repo();
        var account = NewAccount();

        Assert.True(await repo.TryRecordAttendanceAsync(account, 0, 740_000, 1, 0, null));

        Assert.Equal(new[] { "_id", "AttendanceStreakBrokenAfter", "AttendedDays", "LastAttendedDay", "UpdatedAt" }, One(account).Names.ToArray());   // alphabetical, as above
        Assert.Equal(new AccountAttendance(1, 740_000, 0), await repo.GetAttendanceAsync(account));
        Assert.Empty(await repo.GetLiveAsync(account, NowLocal));                   // the missing Gifts reads as an empty list
    }

    [DockerFact]
    public async Task RecordAttendance_first_day_on_a_legacy_box_matches_it_keeps_its_gifts_and_adds_the_attendance_fields()
    {
        var repo = Repo();
        var account = NewAccount();
        var existing = Gift(2);
        Seed(account, new[] { existing }, new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc));
        var reward = Gift(0, source: 4);

        Assert.True(await repo.TryRecordAttendanceAsync(account, 0, 740_000, 1, 0, reward));

        var raw = One(account);
        Assert.Equal(new[] { "_id", "Gifts", "UpdatedAt", "AttendanceStreakBrokenAfter", "AttendedDays", "LastAttendedDay" }, raw.Names.ToArray());   // the new fields are appended, alphabetical
        Assert.Equal(new[] { existing.Guid, reward.Guid }, raw["Gifts"].AsBsonArray.Select(g => g["Guid"].AsString).ToArray());   // pushed at the end
        Assert.True(raw["UpdatedAt"].ToUniversalTime() > new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc));
        Assert.Equal(new AccountAttendance(1, 740_000, 0), await repo.GetAttendanceAsync(account));
        Assert.Equal(1, Count(account));                                            // matched, not a second document
    }

    [DockerFact]
    public async Task RecordAttendance_next_day_matches_only_the_day_the_caller_saw()
    {
        var repo = Repo();
        var account = NewAccount();
        await repo.TryRecordAttendanceAsync(account, 0, 740_000, 1, 0, null);

        // A stale reader (saw another day) loses, and nothing changes.
        Assert.False(await repo.TryRecordAttendanceAsync(account, 739_999, 740_001, 2, 0, Gift(0, source: 4)));
        Assert.Equal(new AccountAttendance(1, 740_000, 0), await repo.GetAttendanceAsync(account));
        Assert.Empty(await repo.GetLiveAsync(account, NowLocal));

        // The reader that saw the stored day wins; the reward is pushed and the streak state is stored.
        var reward = Gift(0, source: 4);
        Assert.True(await repo.TryRecordAttendanceAsync(account, 740_000, 740_003, 2, 1, reward));
        Assert.Equal(new AccountAttendance(2, 740_003, 1), await repo.GetAttendanceAsync(account));
        Assert.Equal(reward.Guid, Assert.Single(await repo.GetLiveAsync(account, NowLocal)).Guid);

        // Replaying the same call loses: the stored day moved on.
        Assert.False(await repo.TryRecordAttendanceAsync(account, 740_000, 740_003, 2, 1, Gift(0, source: 4)));
        Assert.Single(await repo.GetLiveAsync(account, NowLocal));
        Assert.Equal(1, Count(account));
    }

    [DockerFact]
    public async Task RecordAttendance_seen_zero_after_attending_is_false_through_the_DuplicateKey_path_and_changes_nothing()
    {
        var repo = Repo();
        var account = NewAccount();
        await repo.TryRecordAttendanceAsync(account, 0, 740_000, 1, 0, null);
        var before = One(account);

        // seen == 0 upserts, the filter no longer matches the attended box, so the insert collides on _id: DuplicateKey is caught -> false.
        Assert.False(await repo.TryRecordAttendanceAsync(account, 0, 740_001, 2, 0, Gift(0, source: 4)));

        Assert.Equal(before, One(account));
        Assert.Equal(1, Count(account));
    }

    [DockerFact]
    public async Task RecordAttendance_seen_nonzero_without_a_box_is_false_and_creates_nothing()
    {
        var repo = Repo();
        var account = NewAccount();

        Assert.False(await repo.TryRecordAttendanceAsync(account, 740_000, 740_001, 2, 0, Gift(0, source: 4)));   // no upsert when seen != 0

        Assert.Equal(0, Count(account));
        Assert.Equal(new AccountAttendance(0, 0, 0), await repo.GetAttendanceAsync(account));
    }

    [DockerFact]
    public async Task RecordAttendance_concurrent_first_day_has_exactly_one_winner_and_one_reward()
    {
        var repo = Repo();
        var account = NewAccount();

        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ =>
            Task.Run(() => repo.TryRecordAttendanceAsync(account, 0, 740_000, 1, 0, Gift(0, source: 4)))));

        Assert.Equal(1, results.Count(r => r));                                     // the losers hit DuplicateKey (or the filter) and report false
        Assert.Equal(new AccountAttendance(1, 740_000, 0), await repo.GetAttendanceAsync(account));
        Assert.Single(One(account)["Gifts"].AsBsonArray);
        Assert.Equal(1, Count(account));
    }

    [DockerFact]
    public async Task RecordAttendance_concurrent_next_day_has_exactly_one_winner_and_one_reward()
    {
        var repo = Repo();
        var account = NewAccount();
        await repo.TryRecordAttendanceAsync(account, 0, 740_000, 1, 0, null);

        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ =>
            Task.Run(() => repo.TryRecordAttendanceAsync(account, 740_000, 740_001, 2, 0, Gift(0, source: 4)))));

        Assert.Equal(1, results.Count(r => r));
        Assert.Equal(new AccountAttendance(2, 740_001, 0), await repo.GetAttendanceAsync(account));
        Assert.Single(One(account)["Gifts"].AsBsonArray);
    }

    [DockerFact]
    public async Task Filing_and_taking_after_attendance_keep_the_attendance()
    {
        var repo = Repo();
        var account = NewAccount();
        await repo.TryRecordAttendanceAsync(account, 0, 740_000, 4, 2, null);

        var gift = Gift(0);
        Assert.Equal(1, await repo.FileAsync(account, new[] { gift }, NowLocal));
        Assert.Equal(new AccountAttendance(4, 740_000, 2), await repo.GetAttendanceAsync(account));
        Assert.True(await repo.TryTakeAsync(account, gift.Guid));
        Assert.Equal(new AccountAttendance(4, 740_000, 2), await repo.GetAttendanceAsync(account));
    }

    // ------------------------------------------------------------------------------------------------ expiry and claim

    [DockerFact]
    public async Task The_expiry_pull_removes_only_expired_gifts_and_does_not_bump_UpdatedAt()
    {
        var repo = Repo();
        var account = NewAccount();
        var recent = Gift(1);
        var edge = Gift(7);        // the last live day
        var expired = Gift(8);
        var stamp = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
        Seed(account, new[] { expired, recent, edge }, stamp);

        var live = await repo.GetLiveAsync(account, NowLocal);

        Assert.Equal(new[] { recent.Guid, edge.Guid }, live.Select(g => g.Guid).ToArray());   // newest first
        var raw = One(account);
        Assert.Equal(new[] { recent.Guid, edge.Guid }, raw["Gifts"].AsBsonArray.Select(g => g["Guid"].AsString).ToArray());   // stored in stored order, expired pulled
        Assert.Equal(stamp, raw["UpdatedAt"].ToUniversalTime());                              // the pull is NOT a stamped write

        // Nothing expired: no write at all.
        await repo.GetLiveAsync(account, NowLocal);
        Assert.Equal(stamp, One(account)["UpdatedAt"].ToUniversalTime());

        // Filing an already-expired gift counts nothing and the very next read pulls it again (the filing itself bumps UpdatedAt).
        var other = NewAccount();
        Assert.Equal(1, await repo.FileAsync(other, new[] { Gift(7), Gift(8) }, NowLocal));
        Assert.Single(One(other)["Gifts"].AsBsonArray);
    }

    [DockerFact]
    public async Task The_expiry_pull_leaves_other_accounts_untouched()
    {
        var repo = Repo();
        var a = NewAccount();
        var b = NewAccount();
        var expiredB = Gift(9);
        var stamp = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
        Seed(a, new[] { Gift(9), Gift(0) }, stamp);
        Seed(b, new[] { expiredB }, stamp);

        Assert.Single(await repo.GetLiveAsync(a, NowLocal));

        Assert.Equal(expiredB.Guid, Assert.Single(One(b)["Gifts"].AsBsonArray)["Guid"].AsString);
    }

    [DockerFact]
    public async Task TryTake_pulls_by_exact_guid_stamps_UpdatedAt_and_is_true_only_for_the_call_that_removed_it()
    {
        var repo = Repo();
        var account = NewAccount();
        var keep = Gift(0);
        var take = Gift(1);
        var stamp = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
        Seed(account, new[] { keep, take }, stamp);

        Assert.True(await repo.TryTakeAsync(account, take.Guid));
        var raw = One(account);
        Assert.Equal(new[] { keep.Guid }, raw["Gifts"].AsBsonArray.Select(g => g["Guid"].AsString).ToArray());
        var taken = raw["UpdatedAt"].ToUniversalTime();
        Assert.True(taken > stamp);                                                 // unlike the expiry pull, a claim is a stamped write

        // The guid is matched exactly: the lowercase form of a stored uppercase guid pulls nothing and reports false.
        Assert.False(await repo.TryTakeAsync(account, keep.Guid.ToLowerInvariant()));
        Assert.Single(One(account)["Gifts"].AsBsonArray);
        Assert.False(await repo.TryTakeAsync(account, take.Guid));                  // already taken: false, the gift is gone
        Assert.Single(One(account)["Gifts"].AsBsonArray);

        // No box at all: false, and no upsert.
        var absent = NewAccount();
        Assert.False(await repo.TryTakeAsync(absent, take.Guid));
        Assert.Equal(0, Count(absent));

        // Only the caller's account is touched.
        var a = NewAccount();
        var b = NewAccount();
        var gift = Gift(0);
        await repo.FileAsync(a, new[] { gift }, NowLocal);
        await repo.FileAsync(b, new[] { Gift(0) }, NowLocal);
        await repo.TryTakeAsync(b, gift.Guid);
        Assert.Single(One(a)["Gifts"].AsBsonArray);
    }

    [DockerFact]
    public async Task Concurrent_TryTakes_of_one_gift_succeed_exactly_once()
    {
        var repo = Repo();
        var account = NewAccount();
        var gift = Gift(0);
        await repo.FileAsync(account, new[] { gift }, NowLocal);

        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => repo.TryTakeAsync(account, gift.Guid))));

        Assert.Equal(1, results.Count(r => r));
    }

    // ------------------------------------------------------------------------------------------------ the uint -> Int32 bound

    [DockerFact]
    public async Task Uint_ids_above_int_max_round_trip_as_Int64_and_int_max_stays_Int32()
    {
        var repo = Repo();

        // int.MaxValue round-trips as an Int32 (account id in _id, item id in the element).
        var account = (uint)int.MaxValue;
        await repo.FileAsync(account, new[] { Gift(0, itemId: int.MaxValue) }, NowLocal);
        var raw = Raw().Find(new BsonDocument("_id", int.MaxValue)).Single();
        Assert.Equal(BsonType.Int32, raw["_id"].BsonType);
        Assert.Equal(int.MaxValue, raw["Gifts"][0]["ItemId"].AsInt32);
        Assert.Equal((uint)int.MaxValue, Assert.Single(await repo.GetLiveAsync(account, NowLocal)).ItemId);

        // An item id above it is stored as Int64 in the element and reads back exactly.
        var id = NewAccount();
        await repo.FileAsync(id, new[] { Gift(0, itemId: 3_000_000_000u) }, NowLocal);
        Assert.Equal(BsonType.Int64, Raw().Find(new BsonDocument("_id", (int)id)).Single()["Gifts"][0]["ItemId"].BsonType);
        Assert.Equal(3_000_000_000u, Assert.Single(await repo.GetLiveAsync(id, NowLocal)).ItemId);

        // An account id above it is an Int64 _id and works in the read and write filters.
        var bigId = 3_000_000_000u + NewAccount();
        await repo.FileAsync(bigId, new[] { Gift(0) }, NowLocal);
        Assert.Equal(BsonType.Int64, Raw().Find(new BsonDocument("_id", (long)bigId)).Single()["_id"].BsonType);
        Assert.Single(await repo.GetLiveAsync(bigId, NowLocal));
        await repo.GetAttendanceAsync(bigId);
        await repo.TryTakeAsync(bigId, "X");
    }
}
