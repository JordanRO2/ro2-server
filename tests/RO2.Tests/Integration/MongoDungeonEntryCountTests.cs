using RO2.Persistence.Mongo.Persistence;
using RO2.Persistence.Mongo.Repositories;
using Xunit;

namespace RO2.Tests.Integration;

// B18 A-S1: the Mongo twin of PostgresDungeonEntryCountTests, against a REAL mongo:7 container (MongoMailFixture).
// Every uint stays within int.MaxValue (Mongo's UInt32Serializer writes Int32 and throws above it).

[Collection("MongoMail")]
public sealed class MongoDungeonEntryCountTests
{
    private const string Db = "ro2_it_dungeon_entries";
    private static int _ids = 1000;

    private static readonly DateTime Day1 = new(2026, 7, 11, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Day2 = new(2026, 7, 12, 11, 0, 0, DateTimeKind.Utc);

    private readonly MongoMailFixture _fx;
    public MongoDungeonEntryCountTests(MongoMailFixture fx) => _fx = fx;

    private DungeonEntryCountRepository Repo()
    {
        Assert.True(_fx.Available, "Docker is up but the mongo:7 fixture did not start: " + _fx.StartupError);
        return new DungeonEntryCountRepository(new MongoDbContext(_fx.ConnectionString, Db));
    }

    private static uint FreshId() => (uint)Interlocked.Increment(ref _ids);

    [DockerFact]
    public async Task Entries_for_two_dungeons_count_up_and_read_back()
    {
        var repo = Repo();
        var id = FreshId();

        Assert.Equal(0, await repo.GetUsedAsync(id, 11001, Day1));
        Assert.Equal(1, await repo.RecordEntryAsync(id, 11001, Day1));
        Assert.Equal(2, await repo.RecordEntryAsync(id, 11001, Day1.AddMinutes(30)));
        Assert.Equal(1, await repo.RecordEntryAsync(id, 11002, Day1));

        Assert.Equal(2, await repo.GetUsedAsync(id, 11001, Day1.AddHours(5)));
        Assert.Equal(1, await repo.GetUsedAsync(id, 11002, Day1));
        Assert.Equal(0, await repo.GetUsedAsync(id, 11003, Day1));
        Assert.Equal(0, await repo.GetUsedAsync(FreshId(), 11001, Day1));
    }

    [DockerFact]
    public async Task A_new_window_write_wipes_every_prior_count()
    {
        var repo = Repo();
        var id = FreshId();
        await repo.RecordEntryAsync(id, 11001, Day1);
        await repo.RecordEntryAsync(id, 11001, Day1);
        await repo.RecordEntryAsync(id, 11002, Day1);

        Assert.Equal(1, await repo.RecordEntryAsync(id, 11001, Day2));
        Assert.Equal(1, await repo.GetUsedAsync(id, 11001, Day2));
        Assert.Equal(0, await repo.GetUsedAsync(id, 11002, Day2));
        Assert.Equal(2, await repo.RecordEntryAsync(id, 11001, Day2));
    }

    [DockerFact]
    public async Task A_read_in_another_window_returns_zero_without_changing_the_stored_counts()
    {
        var repo = Repo();
        var id = FreshId();
        await repo.RecordEntryAsync(id, 11001, Day2);
        await repo.RecordEntryAsync(id, 11001, Day2);

        Assert.Equal(0, await repo.GetUsedAsync(id, 11001, Day1)); // older window
        Assert.Equal(0, await repo.GetUsedAsync(id, 11001, Day2.AddDays(1))); // newer window
        Assert.Equal(2, await repo.GetUsedAsync(id, 11001, Day2));
    }

    [DockerFact]
    public async Task Reset_boundary_is_exactly_10_utc()
    {
        var repo = Repo();
        var id = FreshId();
        var before = new DateTime(2026, 7, 11, 9, 59, 59, DateTimeKind.Utc);
        var at = new DateTime(2026, 7, 11, 10, 0, 0, DateTimeKind.Utc);

        await repo.RecordEntryAsync(id, 11001, before);
        Assert.Equal(1, await repo.GetUsedAsync(id, 11001, before));
        Assert.Equal(0, await repo.GetUsedAsync(id, 11001, at));
        Assert.Equal(1, await repo.RecordEntryAsync(id, 11001, at));
    }

    [DockerFact]
    public async Task Serial_read_modify_write_accumulates()
    {
        var repo = Repo();
        var id = FreshId();
        for (var i = 1; i <= 15; i++)
            Assert.Equal(i, await repo.RecordEntryAsync(id, 11001, Day1));
        Assert.Equal(15, await repo.GetUsedAsync(id, 11001, Day1));
    }
}
