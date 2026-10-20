using RO2.Domain.Entities;
using RO2.Persistence.Postgres.Repositories;
using Xunit;

namespace RO2.Tests.Integration;

/// <summary>
/// Integration tests for <see cref="RankingRepository"/> against a live PostgreSQL instance.
/// </summary>
/// <remarks>
/// Covers per-period counters with tie-break and concurrency, and snapshot publish with latest-by-period reads. Each test uses a fresh random identifier so tests can share one container.
/// </remarks>
[Collection("Postgres")]
public sealed class PostgresRankingTests
{
    private static long _periodCounter;

    private readonly PostgresFixture _fx;
    public PostgresRankingTests(PostgresFixture fx) => _fx = fx;

    private static DateTime FreshPeriod() =>
        new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(Interlocked.Increment(ref _periodCounter));

    private static string SnapId(byte type, DateTime period) => $"{type}:{period:yyyyMMddHHmm}";

    private static RankingSnapshotDocument Board(byte type, DateTime period, DateTime rolled, params RankingSnapshotRow[] rows) =>
        new()
        {
            Id = SnapId(type, period),
            Type = type,
            PeriodStartUtc = period,
            RolledUtc = rolled,
            Rows = rows.ToList(),
        };

    private static RankingSnapshotRow Row(uint charId, uint rank, long score, string name = "N", string guild = "G") =>
        new()
        {
            CharId = charId,
            Rank = rank,
            PreviousRank = rank + 1,
            ClassId = 3,
            Profession = 7,
            Score = score,
            Name = name,
            Guild = guild,
        };

    [Fact]
    public async Task Running_score_is_zero_when_none_stored()
    {
        if (!_fx.Available) return;
        var repo = new RankingRepository(_fx.Db!);
        Assert.Equal(0, await repo.GetRunningScoreAsync(1, FreshPeriod(), 42));
    }

    [Fact]
    public async Task AddScore_creates_then_accumulates_and_moves_reached_time()
    {
        if (!_fx.Available) return;
        var repo = new RankingRepository(_fx.Db!);
        var period = FreshPeriod();
        var t1 = new DateTime(2030, 5, 1, 10, 0, 0, DateTimeKind.Utc);
        var t2 = t1.AddMinutes(5);

        await repo.AddScoreAsync(2, period, 4_000_000_000u, 100, t1); 
        Assert.Equal(100, await repo.GetRunningScoreAsync(2, period, 4_000_000_000u));

        await repo.AddScoreAsync(2, period, 4_000_000_000u, 250, t2);
        Assert.Equal(350, await repo.GetRunningScoreAsync(2, period, 4_000_000_000u));

        var scores = await repo.GetPeriodScoresAsync(2, period);
        var only = Assert.Single(scores);
        Assert.Equal(4_000_000_000u, only.CharId);
        Assert.Equal(350, only.Score);
        Assert.Equal(t2, only.ReachedUtc);          
        Assert.Equal(DateTimeKind.Utc, only.ReachedUtc.Kind);
    }

    [Fact]
    public async Task AddScore_negative_amount_decrements()
    {
        if (!_fx.Available) return;
        var repo = new RankingRepository(_fx.Db!);
        var period = FreshPeriod();
        var now = DateTime.UtcNow;
        await repo.AddScoreAsync(3, period, 9, 500, now);
        await repo.AddScoreAsync(3, period, 9, -200, now);
        Assert.Equal(300, await repo.GetRunningScoreAsync(3, period, 9));
    }

    [Fact]
    public async Task Counters_are_isolated_by_type_period_and_character()
    {
        if (!_fx.Available) return;
        var repo = new RankingRepository(_fx.Db!);
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
    }

    [Fact]
    public async Task GetPeriodScores_returns_every_counter_of_the_period_only()
    {
        if (!_fx.Available) return;
        var repo = new RankingRepository(_fx.Db!);
        var period = FreshPeriod();
        var other = FreshPeriod();
        var t = new DateTime(2031, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        await repo.AddScoreAsync(6, period, 11, 5, t);
        await repo.AddScoreAsync(6, period, 12, 15, t.AddSeconds(1));
        await repo.AddScoreAsync(6, other, 13, 25, t);      
        await repo.AddScoreAsync(7, period, 14, 35, t);     

        var scores = (await repo.GetPeriodScoresAsync(6, period)).OrderBy(s => s.CharId).ToList();
        Assert.Equal(2, scores.Count);
        Assert.Equal(new RankingScore(11, 5, t), scores[0]);
        Assert.Equal(new RankingScore(12, 15, t.AddSeconds(1)), scores[1]);
    }

    [Fact]
    public async Task GetPeriodScores_is_empty_when_none()
    {
        if (!_fx.Available) return;
        var repo = new RankingRepository(_fx.Db!);
        Assert.Empty(await repo.GetPeriodScoresAsync(8, FreshPeriod()));
    }

    [Fact]
    public async Task Reached_time_is_truncated_to_milliseconds_like_a_bson_date()
    {
        if (!_fx.Available) return;
        var repo = new RankingRepository(_fx.Db!);
        var period = FreshPeriod();
        var reached = new DateTime(2032, 6, 7, 8, 9, 10, DateTimeKind.Utc).AddTicks(1234_567); 
        await repo.AddScoreAsync(9, period, 77, 1, reached);

        var only = Assert.Single(await repo.GetPeriodScoresAsync(9, period));
        Assert.Equal(new DateTime(2032, 6, 7, 8, 9, 10, DateTimeKind.Utc).AddMilliseconds(123), only.ReachedUtc);
    }

    [Fact]
    public async Task Concurrent_AddScore_loses_no_increment()
    {
        if (!_fx.Available) return;
        var repo = new RankingRepository(_fx.Db!);
        var period = FreshPeriod();
        var now = DateTime.UtcNow;

        await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => repo.AddScoreAsync(10, period, 5, 3, now)));

        Assert.Equal(120, await repo.GetRunningScoreAsync(10, period, 5));
        Assert.Single(await repo.GetPeriodScoresAsync(10, period));
    }

    [Fact]
    public async Task Latest_snapshot_is_null_when_none_published()
    {
        if (!_fx.Available) return;
        var repo = new RankingRepository(_fx.Db!);
        Assert.Null(await repo.GetLatestSnapshotAsync(201));
    }

    [Fact]
    public async Task Publish_inserts_returns_candidate_and_round_trips_every_field()
    {
        if (!_fx.Available) return;
        var repo = new RankingRepository(_fx.Db!);
        var period = new DateTime(2033, 3, 4, 5, 0, 0, DateTimeKind.Utc);
        var rolled = new DateTime(2033, 3, 4, 5, 0, 1, DateTimeKind.Utc);
        var candidate = Board(202, period, rolled,
            Row(4_000_000_000u, 1, 9_000_000_000L, "Alpha", "GuildA"),
            Row(2, 2, 50, "Beta", ""));

        var published = await repo.PublishSnapshotAsync(candidate);
        Assert.Same(candidate, published);

        var latest = await repo.GetLatestSnapshotAsync(202);
        Assert.NotNull(latest);
        Assert.Equal(SnapId(202, period), latest!.Id);
        Assert.Equal(202, latest.Type);
        Assert.Equal(period, latest.PeriodStartUtc);
        Assert.Equal(DateTimeKind.Utc, latest.PeriodStartUtc.Kind);
        Assert.Equal(rolled, latest.RolledUtc);
        Assert.Equal(2, latest.Rows.Count);

        var first = latest.Rows[0];                  
        Assert.Equal(4_000_000_000u, first.CharId);
        Assert.Equal(1u, first.Rank);
        Assert.Equal(2u, first.PreviousRank);
        Assert.Equal(3, first.ClassId);
        Assert.Equal(7, first.Profession);
        Assert.Equal(9_000_000_000L, first.Score);
        Assert.Equal("Alpha", first.Name);
        Assert.Equal("GuildA", first.Guild);

        var second = latest.Rows[1];
        Assert.Equal(2u, second.CharId);
        Assert.Equal("Beta", second.Name);
        Assert.Equal("", second.Guild);
    }

    [Fact]
    public async Task Publish_empty_board_round_trips_empty()
    {
        if (!_fx.Available) return;
        var repo = new RankingRepository(_fx.Db!);
        var period = new DateTime(2034, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await repo.PublishSnapshotAsync(Board(203, period, period));

        var latest = await repo.GetLatestSnapshotAsync(203);
        Assert.NotNull(latest);
        Assert.Empty(latest!.Rows);
    }

    [Fact]
    public async Task Publish_same_id_first_writer_wins_and_returns_the_stored_board()
    {
        if (!_fx.Available) return;
        var repo = new RankingRepository(_fx.Db!);
        var period = new DateTime(2035, 2, 3, 4, 0, 0, DateTimeKind.Utc);
        var rolled1 = period.AddSeconds(1);
        var first = Board(204, period, rolled1, Row(1, 1, 100, "First"));
        var second = Board(204, period, period.AddSeconds(9), Row(2, 1, 999, "Second"), Row(3, 2, 5, "Third"));

        Assert.Same(first, await repo.PublishSnapshotAsync(first));

        var result = await repo.PublishSnapshotAsync(second);
        Assert.NotSame(second, result);               
        Assert.Equal(SnapId(204, period), result.Id);
        Assert.Equal(rolled1, result.RolledUtc);
        Assert.Equal(DateTimeKind.Utc, result.PeriodStartUtc.Kind);
        var winnerRow = Assert.Single(result.Rows);
        Assert.Equal(1u, winnerRow.CharId);
        Assert.Equal("First", winnerRow.Name);

        
        var stored = await repo.GetLatestSnapshotAsync(204);
        Assert.Equal(rolled1, stored!.RolledUtc);
        Assert.Single(stored.Rows);
    }

    [Fact]
    public async Task Concurrent_publish_of_one_id_has_exactly_one_winner()
    {
        if (!_fx.Available) return;
        var repo = new RankingRepository(_fx.Db!);
        var period = new DateTime(2036, 4, 5, 6, 0, 0, DateTimeKind.Utc);
        var candidates = Enumerable.Range(1, 12)
            .Select(i => Board(205, period, period.AddSeconds(i), Row((uint)i, 1, i)))
            .ToList();

        var results = await Task.WhenAll(candidates.Select(c => repo.PublishSnapshotAsync(c)));

        var stored = await repo.GetLatestSnapshotAsync(205);
        Assert.NotNull(stored);
        var storedCharId = Assert.Single(stored!.Rows).CharId;

        
        Assert.Equal(1, results.Select((r, i) => ReferenceEquals(r, candidates[i])).Count(x => x));
        Assert.All(results, r => Assert.Equal(storedCharId, Assert.Single(r.Rows).CharId));
    }

    [Fact]
    public async Task Latest_snapshot_is_the_one_with_the_latest_period_regardless_of_publish_order()
    {
        if (!_fx.Available) return;
        var repo = new RankingRepository(_fx.Db!);
        var p1 = new DateTime(2037, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var p2 = p1.AddDays(7);
        var p3 = p1.AddDays(14);

        await repo.PublishSnapshotAsync(Board(206, p2, p2, Row(2, 1, 2)));
        await repo.PublishSnapshotAsync(Board(206, p3, p3, Row(3, 1, 3)));
        await repo.PublishSnapshotAsync(Board(206, p1, p1, Row(1, 1, 1)));   

        var latest = await repo.GetLatestSnapshotAsync(206);
        Assert.NotNull(latest);
        Assert.Equal(p3, latest!.PeriodStartUtc);
        Assert.Equal(3u, Assert.Single(latest.Rows).CharId);
    }

    [Fact]
    public async Task Latest_snapshot_is_scoped_to_the_ranking_type()
    {
        if (!_fx.Available) return;
        var repo = new RankingRepository(_fx.Db!);
        var period = new DateTime(2038, 8, 9, 0, 0, 0, DateTimeKind.Utc);

        await repo.PublishSnapshotAsync(Board(207, period, period, Row(70, 1, 7)));
        await repo.PublishSnapshotAsync(Board(208, period.AddDays(30), period, Row(80, 1, 8)));

        var a = await repo.GetLatestSnapshotAsync(207);
        var b = await repo.GetLatestSnapshotAsync(208);
        Assert.Equal(70u, Assert.Single(a!.Rows).CharId);
        Assert.Equal(80u, Assert.Single(b!.Rows).CharId);
        Assert.Null(await repo.GetLatestSnapshotAsync(209));  
    }
}
