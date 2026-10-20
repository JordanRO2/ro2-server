using RO2.Persistence.Postgres.Repositories;
using Xunit;

namespace RO2.Tests.Integration;

/// <summary>
/// Runtime tests for <see cref="DungeonEntryCountRepository"/> against a live PostgreSQL instance
/// (<see cref="PostgresFixture"/>). Covers zero reads for unknown characters, per-DungeonID counting, the lazy
/// daily reset at 10:00 UTC, per-character isolation, the uint-to-bigint round-trip, and an atomic burst of entries.
/// </summary>
/// <remarks>
/// On reset, the count reads as zero, the next entry restarts at one, and other dungeons' counts are dropped.
/// Each test uses a fresh random character id so tests can share one container.
/// </remarks>
[Collection("Postgres")]
public sealed class PostgresDungeonEntryCountTests
{
    private readonly PostgresFixture _fx;
    public PostgresDungeonEntryCountTests(PostgresFixture fx) => _fx = fx;

    private static uint FreshId() => (uint)Random.Shared.NextInt64(1, uint.MaxValue);

    
    private static readonly DateTime Day1 = new(2026, 7, 11, 12, 0, 0, DateTimeKind.Utc);
    
    private static readonly DateTime Day2 = new(2026, 7, 12, 11, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void WindowStart_is_the_latest_10_utc_at_or_before_now()
    {
        Assert.Equal(new DateTime(2026, 7, 10, 10, 0, 0, DateTimeKind.Utc),
            DungeonEntryCountRepository.WindowStart(new DateTime(2026, 7, 11, 2, 50, 45, DateTimeKind.Utc)));
        Assert.Equal(new DateTime(2026, 7, 11, 10, 0, 0, DateTimeKind.Utc),
            DungeonEntryCountRepository.WindowStart(new DateTime(2026, 7, 11, 12, 21, 55, DateTimeKind.Utc)));
        Assert.Equal(new DateTime(2026, 7, 11, 10, 0, 0, DateTimeKind.Utc),
            DungeonEntryCountRepository.WindowStart(new DateTime(2026, 7, 11, 10, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(new DateTime(2026, 7, 10, 10, 0, 0, DateTimeKind.Utc),
            DungeonEntryCountRepository.WindowStart(new DateTime(2026, 7, 11, 9, 59, 59, DateTimeKind.Utc)));
    }

    [Fact]
    public async Task Unknown_character_reads_zero()
    {
        if (!_fx.Available) return;
        var repo = new DungeonEntryCountRepository(_fx.Db!);
        Assert.Equal(0, await repo.GetUsedAsync(FreshId(), 11001, Day1));
    }

    [Fact]
    public async Task Record_counts_up_and_get_reads_it_back()
    {
        if (!_fx.Available) return;
        var repo = new DungeonEntryCountRepository(_fx.Db!);
        uint id = FreshId();

        Assert.Equal(1, await repo.RecordEntryAsync(id, 11001, Day1));
        Assert.Equal(2, await repo.RecordEntryAsync(id, 11001, Day1));
        Assert.Equal(3, await repo.RecordEntryAsync(id, 11001, Day1.AddMinutes(30)));

        Assert.Equal(3, await repo.GetUsedAsync(id, 11001, Day1));
        Assert.Equal(3, await repo.GetUsedAsync(id, 11001, Day1.AddHours(5)));
    }

    [Fact]
    public async Task Counts_are_per_dungeon()
    {
        if (!_fx.Available) return;
        var repo = new DungeonEntryCountRepository(_fx.Db!);
        uint id = FreshId();

        await repo.RecordEntryAsync(id, 11001, Day1);
        await repo.RecordEntryAsync(id, 11001, Day1);
        Assert.Equal(1, await repo.RecordEntryAsync(id, 11002, Day1));

        Assert.Equal(2, await repo.GetUsedAsync(id, 11001, Day1));
        Assert.Equal(1, await repo.GetUsedAsync(id, 11002, Day1));
        Assert.Equal(0, await repo.GetUsedAsync(id, 11003, Day1));
    }

    [Fact]
    public async Task Counts_are_per_character()
    {
        if (!_fx.Available) return;
        var repo = new DungeonEntryCountRepository(_fx.Db!);
        uint a = FreshId();
        uint b = FreshId();

        await repo.RecordEntryAsync(a, 11001, Day1);
        await repo.RecordEntryAsync(a, 11001, Day1);
        Assert.Equal(1, await repo.RecordEntryAsync(b, 11001, Day1));

        Assert.Equal(2, await repo.GetUsedAsync(a, 11001, Day1));
        Assert.Equal(1, await repo.GetUsedAsync(b, 11001, Day1));
    }

    [Fact]
    public async Task Next_window_reads_zero_without_touching_the_stored_counts()
    {
        if (!_fx.Available) return;
        var repo = new DungeonEntryCountRepository(_fx.Db!);
        uint id = FreshId();
        await repo.RecordEntryAsync(id, 11001, Day1);
        await repo.RecordEntryAsync(id, 11001, Day1);

        Assert.Equal(0, await repo.GetUsedAsync(id, 11001, Day2));
        Assert.Equal(2, await repo.GetUsedAsync(id, 11001, Day1)); 
    }

    [Fact]
    public async Task Record_in_next_window_restarts_at_one_and_drops_other_dungeons()
    {
        if (!_fx.Available) return;
        var repo = new DungeonEntryCountRepository(_fx.Db!);
        uint id = FreshId();
        await repo.RecordEntryAsync(id, 11001, Day1);
        await repo.RecordEntryAsync(id, 11001, Day1);
        await repo.RecordEntryAsync(id, 11002, Day1);

        Assert.Equal(1, await repo.RecordEntryAsync(id, 11001, Day2));
        Assert.Equal(1, await repo.GetUsedAsync(id, 11001, Day2));
        Assert.Equal(0, await repo.GetUsedAsync(id, 11002, Day2)); 
        Assert.Equal(2, await repo.RecordEntryAsync(id, 11001, Day2));
    }

    [Fact]
    public async Task Record_in_an_earlier_window_replaces_the_stored_one()
    {
        if (!_fx.Available) return;
        var repo = new DungeonEntryCountRepository(_fx.Db!);
        uint id = FreshId();
        await repo.RecordEntryAsync(id, 11001, Day2);
        await repo.RecordEntryAsync(id, 11001, Day2);

        
        Assert.Equal(1, await repo.RecordEntryAsync(id, 11001, Day1));
        Assert.Equal(1, await repo.GetUsedAsync(id, 11001, Day1));
        Assert.Equal(0, await repo.GetUsedAsync(id, 11001, Day2));
    }

    [Fact]
    public async Task Reset_boundary_is_exactly_10_utc()
    {
        if (!_fx.Available) return;
        var repo = new DungeonEntryCountRepository(_fx.Db!);
        uint id = FreshId();
        var before = new DateTime(2026, 7, 11, 9, 59, 59, DateTimeKind.Utc);
        var at = new DateTime(2026, 7, 11, 10, 0, 0, DateTimeKind.Utc);

        await repo.RecordEntryAsync(id, 11001, before);
        await repo.RecordEntryAsync(id, 11001, before);
        Assert.Equal(2, await repo.GetUsedAsync(id, 11001, before));
        Assert.Equal(0, await repo.GetUsedAsync(id, 11001, at));
        Assert.Equal(1, await repo.RecordEntryAsync(id, 11001, at));
    }

    [Fact]
    public async Task Local_and_unspecified_kinds_are_read_as_their_utc_instant()
    {
        if (!_fx.Available) return;
        var repo = new DungeonEntryCountRepository(_fx.Db!);
        uint id = FreshId();
        await repo.RecordEntryAsync(id, 11001, Day1);

        Assert.Equal(1, await repo.GetUsedAsync(id, 11001, Day1.ToLocalTime())); 
        Assert.Equal(1, await repo.GetUsedAsync(id, 11001, DateTime.SpecifyKind(Day1, DateTimeKind.Unspecified))); 
        Assert.Equal(2, await repo.RecordEntryAsync(id, 11001, Day1.ToLocalTime()));
    }

    [Fact]
    public async Task Uint_ids_above_int_max_round_trip()
    {
        if (!_fx.Available) return;
        var repo = new DungeonEntryCountRepository(_fx.Db!);
        uint id = uint.MaxValue - (uint)Random.Shared.Next(0, 1_000_000);
        uint dungeon = 4_000_000_000;

        Assert.Equal(1, await repo.RecordEntryAsync(id, dungeon, Day1));
        Assert.Equal(2, await repo.RecordEntryAsync(id, dungeon, Day1));
        Assert.Equal(2, await repo.GetUsedAsync(id, dungeon, Day1));
        Assert.Equal(0, await repo.GetUsedAsync(id, 4_000_000_001, Day1));
    }

    [Fact]
    public async Task Concurrent_records_of_one_character_lose_no_count()
    {
        if (!_fx.Available) return;
        var repo = new DungeonEntryCountRepository(_fx.Db!);
        uint id = FreshId();
        await repo.RecordEntryAsync(id, 11002, Day1); 

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => repo.RecordEntryAsync(id, 11001, Day1)));

        Assert.Equal(Enumerable.Range(1, 20), results.OrderBy(r => r));
        Assert.Equal(20, await repo.GetUsedAsync(id, 11001, Day1));
        Assert.Equal(1, await repo.GetUsedAsync(id, 11002, Day1));
    }

    [Fact]
    public async Task Concurrent_first_records_of_one_character_lose_no_count()
    {
        if (!_fx.Available) return;
        var repo = new DungeonEntryCountRepository(_fx.Db!);
        uint id = FreshId();

        
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => repo.RecordEntryAsync(id, 11001, Day1)));

        Assert.Equal(Enumerable.Range(1, 10), results.OrderBy(r => r));
        Assert.Equal(10, await repo.GetUsedAsync(id, 11001, Day1));
    }
}
