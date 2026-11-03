using MongoDB.Driver;
using RO2.Domain.Entities;
using RO2.Rankings.Application;
using RO2.Rankings.Domain;
using RO2.Persistence.Mongo.Persistence;
using RO2.Rankings.Infrastructure.Mongo;
using Xunit;

namespace RO2.Tests.Integration;

// B17 S1: the Mongo twin of PostgresRankingTests, against a REAL mongo:7 container (MongoMailFixture). Same rules, same values.
// uint is Int32 up to int.MaxValue and Int64 above (LenientUInt32Serializer); the ids here stay small.

[Collection("MongoMail")]
public sealed class MongoRankingTests
{
    private const string Db = "ro2_it_ranking";
    private static long _periodCounter;

    private readonly MongoMailFixture _fx;
    public MongoRankingTests(MongoMailFixture fx) => _fx = fx;

    private RankingRepository Repo()
    {
        Assert.True(_fx.Available, "Docker is up but the mongo:7 fixture did not start: " + _fx.StartupError);
        return new RankingRepository(new MongoDbContext(_fx.ConnectionString, Db));
    }

    private static DateTime FreshPeriod() =>
        new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(Interlocked.Increment(ref _periodCounter));

    private static string SnapId(byte type, DateTime period) => $"{type}:{period:yyyyMMddHHmm}";

    private static RankingSnapshotDocument Board(byte type, DateTime period, DateTime rolled, params RankingSnapshotRow[] rows) =>
        new() { Id = SnapId(type, period), Type = type, PeriodStartUtc = period, RolledUtc = rolled, Rows = rows.ToList() };

    private static RankingSnapshotRow Row(uint charId, uint rank, long score, string name = "N", string guild = "G") =>
        new() { CharId = charId, Rank = rank, PreviousRank = rank + 1, ClassId = 3, Profession = 7, Score = score, Name = name, Guild = guild };

    [DockerFact]
    public async Task AddScore_creates_then_accumulates_and_moves_reached_time()
    {
        var repo = Repo();
        var period = FreshPeriod();
        var t1 = new DateTime(2030, 5, 1, 10, 0, 0, DateTimeKind.Utc);
        var t2 = t1.AddMinutes(5);

        await repo.AddScoreAsync(2, period, 2_000_000_000u, 100, t1);
        Assert.Equal(100, await repo.GetRunningScoreAsync(2, period, 2_000_000_000u));
        await repo.AddScoreAsync(2, period, 2_000_000_000u, 250, t2);
        Assert.Equal(350, await repo.GetRunningScoreAsync(2, period, 2_000_000_000u));

        var only = Assert.Single(await repo.GetPeriodScoresAsync(2, period));
        Assert.Equal(2_000_000_000u, only.CharId);
        Assert.Equal(350, only.Score);
        Assert.Equal(t2, only.ReachedUtc);
        Assert.Equal(DateTimeKind.Utc, only.ReachedUtc.Kind);
    }

    [DockerFact]
    public async Task AddScore_negative_amount_decrements()
    {
        var repo = Repo();
        var period = FreshPeriod();
        var now = DateTime.UtcNow;
        await repo.AddScoreAsync(3, period, 9, 500, now);
        await repo.AddScoreAsync(3, period, 9, -200, now);
        Assert.Equal(300, await repo.GetRunningScoreAsync(3, period, 9));
    }

    [DockerFact]
    public async Task Running_score_is_zero_and_period_scores_empty_when_none()
    {
        var repo = Repo();
        var period = FreshPeriod();
        Assert.Equal(0, await repo.GetRunningScoreAsync(1, period, 42));
        Assert.Empty(await repo.GetPeriodScoresAsync(8, period));
    }

    [DockerFact]
    public async Task Counters_are_isolated_by_type_period_and_character()
    {
        var repo = Repo();
        var period = FreshPeriod();
        var other = FreshPeriod();
        var now = DateTime.UtcNow;
        await repo.AddScoreAsync(4, period, 1, 10, now);
        await repo.AddScoreAsync(5, period, 1, 20, now);
        await repo.AddScoreAsync(4, other, 1, 30, now);
        await repo.AddScoreAsync(4, period, 2, 40, now);

        Assert.Equal(10, await repo.GetRunningScoreAsync(4, period, 1));
        Assert.Equal(20, await repo.GetRunningScoreAsync(5, period, 1));
        Assert.Equal(30, await repo.GetRunningScoreAsync(4, other, 1));
        Assert.Equal(40, await repo.GetRunningScoreAsync(4, period, 2));
        Assert.Equal(0, await repo.GetRunningScoreAsync(4, period, 3));
        Assert.Equal(2, (await repo.GetPeriodScoresAsync(4, period)).Count);
    }

    [DockerFact]
    public async Task Reached_time_is_truncated_to_milliseconds()
    {
        var repo = Repo();
        var period = FreshPeriod();
        var reached = new DateTime(2032, 6, 7, 8, 9, 10, DateTimeKind.Utc).AddTicks(1234_567);
        await repo.AddScoreAsync(9, period, 77, 1, reached);
        var only = Assert.Single(await repo.GetPeriodScoresAsync(9, period));
        Assert.Equal(new DateTime(2032, 6, 7, 8, 9, 10, DateTimeKind.Utc).AddMilliseconds(123), only.ReachedUtc);
    }

    [DockerFact]
    public async Task Latest_snapshot_is_null_when_none_published()
    {
        Assert.Null(await Repo().GetLatestSnapshotAsync(201));
    }

    [DockerFact]
    public async Task Publish_inserts_returns_candidate_and_round_trips_every_field()
    {
        var repo = Repo();
        var period = new DateTime(2033, 3, 4, 5, 0, 0, DateTimeKind.Utc);
        var rolled = new DateTime(2033, 3, 4, 5, 0, 1, DateTimeKind.Utc);
        var candidate = Board(202, period, rolled, Row(2_000_000_000u, 1, 9_000_000_000L, "Alpha", "GuildA"), Row(2, 2, 50, "Beta", ""));

        Assert.Same(candidate, await repo.PublishSnapshotAsync(candidate));

        var latest = await repo.GetLatestSnapshotAsync(202);
        Assert.NotNull(latest);
        Assert.Equal(SnapId(202, period), latest!.Id);
        Assert.Equal(202, latest.Type);
        Assert.Equal(period, latest.PeriodStartUtc);
        Assert.Equal(DateTimeKind.Utc, latest.PeriodStartUtc.Kind);
        Assert.Equal(rolled, latest.RolledUtc);
        Assert.Equal(2, latest.Rows.Count);
        var first = latest.Rows[0];
        Assert.Equal(2_000_000_000u, first.CharId);
        Assert.Equal(1u, first.Rank);
        Assert.Equal(2u, first.PreviousRank);
        Assert.Equal(3, first.ClassId);
        Assert.Equal(7, first.Profession);
        Assert.Equal(9_000_000_000L, first.Score);
        Assert.Equal("Alpha", first.Name);
        Assert.Equal("GuildA", first.Guild);
        Assert.Equal("Beta", latest.Rows[1].Name);
        Assert.Equal("", latest.Rows[1].Guild);
    }

    [DockerFact]
    public async Task Publish_same_id_first_writer_wins_and_returns_the_stored_board()
    {
        var repo = Repo();
        var period = new DateTime(2035, 2, 3, 4, 0, 0, DateTimeKind.Utc);
        var rolled1 = period.AddSeconds(1);
        var first = Board(204, period, rolled1, Row(1, 1, 100, "First"));
        var second = Board(204, period, period.AddSeconds(9), Row(2, 1, 999, "Second"), Row(3, 2, 5, "Third"));

        Assert.Same(first, await repo.PublishSnapshotAsync(first));

        var result = await repo.PublishSnapshotAsync(second);
        Assert.NotSame(second, result);
        Assert.Equal(rolled1, result.RolledUtc);
        Assert.Equal(DateTimeKind.Utc, result.PeriodStartUtc.Kind);
        Assert.Equal("First", Assert.Single(result.Rows).Name);

        var stored = await repo.GetLatestSnapshotAsync(204);
        Assert.Equal(rolled1, stored!.RolledUtc);
        Assert.Single(stored.Rows);
    }

    [DockerFact]
    public async Task Latest_snapshot_is_the_latest_period_regardless_of_publish_order_and_scoped_to_type()
    {
        var repo = Repo();
        var p1 = new DateTime(2037, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await repo.PublishSnapshotAsync(Board(206, p1.AddDays(7), p1, Row(2, 1, 2)));
        await repo.PublishSnapshotAsync(Board(206, p1.AddDays(14), p1, Row(3, 1, 3)));
        await repo.PublishSnapshotAsync(Board(206, p1, p1, Row(1, 1, 1)));
        await repo.PublishSnapshotAsync(Board(207, p1.AddDays(30), p1, Row(70, 1, 7)));

        Assert.Equal(3u, Assert.Single((await repo.GetLatestSnapshotAsync(206))!.Rows).CharId);
        Assert.Equal(70u, Assert.Single((await repo.GetLatestSnapshotAsync(207))!.Rows).CharId);
        Assert.Null(await repo.GetLatestSnapshotAsync(209));
    }
}
