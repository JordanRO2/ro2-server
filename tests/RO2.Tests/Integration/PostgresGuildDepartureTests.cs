using RO2.Persistence.Postgres;
using RO2.Persistence.Postgres.Repositories;
using Xunit;

namespace RO2.Tests.Integration;

/// <summary>
/// Runtime tests for <see cref="GuildDepartureRepository"/> against a live PostgreSQL instance
/// (<see cref="PostgresFixture"/>). Covers memory-first reads, whole-row write-through that survives a restart
/// (a second repository instance loads the table), the once-only kick notice, independence of the two fields,
/// load-once initialization, and memory-only mode before a successful init.
/// </summary>
/// <remarks>Each test uses a fresh random character id so tests can share one container.</remarks>
[Collection("Postgres")]
public sealed class PostgresGuildDepartureTests
{
    private readonly PostgresFixture _fx;
    public PostgresGuildDepartureTests(PostgresFixture fx) => _fx = fx;

    
    private static uint FreshCharId() => (uint)Random.Shared.NextInt64(int.MaxValue + 1L, uint.MaxValue);

    private async Task<GuildDepartureRepository> StartAsync()
    {
        var repo = new GuildDepartureRepository(_fx.Db!);
        await repo.InitAsync();
        return repo;
    }

    [Fact]
    public async Task Unknown_character_has_no_leave_and_no_notice()
    {
        if (!_fx.Available) return;
        var repo = await StartAsync();
        var id = FreshCharId();
        Assert.Null(repo.LeftUtcOf(id));
        Assert.False(await repo.TakeKickNoticeAsync(id));
    }

    [Fact]
    public async Task RecordLeave_is_read_back_and_survives_a_restart()
    {
        if (!_fx.Available) return;
        var id = FreshCharId();
        var left = new DateTime(2026, 9, 20, 15, 26, 23, 500, DateTimeKind.Utc);
        var repo = await StartAsync();

        await repo.RecordLeaveAsync(id, left);
        Assert.Equal(left, repo.LeftUtcOf(id));

        var restarted = await StartAsync();
        var loaded = restarted.LeftUtcOf(id);
        Assert.Equal(left, loaded);
        Assert.Equal(DateTimeKind.Utc, loaded!.Value.Kind);
        Assert.False(await restarted.TakeKickNoticeAsync(id)); 
    }

    [Fact]
    public async Task RecordLeave_twice_keeps_the_latest()
    {
        if (!_fx.Available) return;
        var id = FreshCharId();
        var first = new DateTime(2026, 9, 20, 10, 0, 0, DateTimeKind.Utc);
        var second = new DateTime(2026, 9, 21, 11, 30, 0, DateTimeKind.Utc);
        var repo = await StartAsync();

        await repo.RecordLeaveAsync(id, first);
        await repo.RecordLeaveAsync(id, second);

        Assert.Equal(second, repo.LeftUtcOf(id));
        Assert.Equal(second, (await StartAsync()).LeftUtcOf(id));
    }

    [Fact]
    public async Task RecordLeave_stores_whole_milliseconds_and_converts_to_utc()
    {
        if (!_fx.Available) return;
        var id = FreshCharId();
        var repo = await StartAsync();
        var withTicks = new DateTime(2026, 9, 20, 15, 26, 23, DateTimeKind.Utc).AddTicks(12_345); 
        var offset = new DateTimeOffset(2026, 9, 20, 17, 26, 23, TimeSpan.FromHours(2)).LocalDateTime;

        await repo.RecordLeaveAsync(id, withTicks);
        Assert.Equal(withTicks, repo.LeftUtcOf(id));                     
        var loaded = (await StartAsync()).LeftUtcOf(id);
        Assert.Equal(new DateTime(2026, 9, 20, 15, 26, 23, 1, DateTimeKind.Utc), loaded); 

        var other = FreshCharId();
        await repo.RecordLeaveAsync(other, offset);                      
        Assert.Equal(new DateTime(2026, 9, 20, 15, 26, 23, DateTimeKind.Utc), (await StartAsync()).LeftUtcOf(other));
    }

    [Fact]
    public async Task MarkKickNotice_is_taken_once()
    {
        if (!_fx.Available) return;
        var id = FreshCharId();
        var repo = await StartAsync();

        Assert.False(await repo.TakeKickNoticeAsync(id));
        await repo.MarkKickNoticeAsync(id);
        Assert.True(await repo.TakeKickNoticeAsync(id));
        Assert.False(await repo.TakeKickNoticeAsync(id));
    }

    [Fact]
    public async Task Pending_notice_survives_a_restart_and_is_then_taken_once()
    {
        if (!_fx.Available) return;
        var id = FreshCharId();
        await (await StartAsync()).MarkKickNoticeAsync(id);

        var restarted = await StartAsync();
        Assert.True(await restarted.TakeKickNoticeAsync(id));
        Assert.False(await restarted.TakeKickNoticeAsync(id));
    }

    [Fact]
    public async Task Taken_notice_is_cleared_in_the_database()
    {
        if (!_fx.Available) return;
        var id = FreshCharId();
        var repo = await StartAsync();
        await repo.MarkKickNoticeAsync(id);
        Assert.True(await repo.TakeKickNoticeAsync(id));

        
        Assert.False(await (await StartAsync()).TakeKickNoticeAsync(id));
    }

    [Fact]
    public async Task Leave_and_notice_are_independent_fields()
    {
        if (!_fx.Available) return;
        var id = FreshCharId();
        var left = new DateTime(2026, 9, 20, 15, 26, 23, 500, DateTimeKind.Utc);
        var repo = await StartAsync();

        await repo.RecordLeaveAsync(id, left);
        await repo.MarkKickNoticeAsync(id);                   
        Assert.Equal(left, repo.LeftUtcOf(id));
        Assert.True(await repo.TakeKickNoticeAsync(id));      
        Assert.Equal(left, repo.LeftUtcOf(id));

        var restarted = await StartAsync();
        Assert.Equal(left, restarted.LeftUtcOf(id));
        Assert.False(await restarted.TakeKickNoticeAsync(id));

        
        var other = FreshCharId();
        await repo.MarkKickNoticeAsync(other);
        await repo.RecordLeaveAsync(other, left);
        var again = await StartAsync();
        Assert.Equal(left, again.LeftUtcOf(other));
        Assert.True(await again.TakeKickNoticeAsync(other));
    }

    [Fact]
    public async Task Racing_takes_yield_exactly_one_notice()
    {
        if (!_fx.Available) return;
        var id = FreshCharId();
        var repo = await StartAsync();
        await repo.MarkKickNoticeAsync(id);

        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => repo.TakeKickNoticeAsync(id))));

        Assert.Equal(1, results.Count(r => r));
        Assert.False(await (await StartAsync()).TakeKickNoticeAsync(id));
    }

    [Fact]
    public async Task Characters_are_independent()
    {
        if (!_fx.Available) return;
        var a = FreshCharId();
        var b = FreshCharId();
        var left = new DateTime(2026, 9, 20, 8, 0, 0, DateTimeKind.Utc);
        var repo = await StartAsync();

        await repo.RecordLeaveAsync(a, left);
        await repo.MarkKickNoticeAsync(b);

        Assert.Equal(left, repo.LeftUtcOf(a));
        Assert.Null(repo.LeftUtcOf(b));
        Assert.False(await repo.TakeKickNoticeAsync(a));
        Assert.True(await repo.TakeKickNoticeAsync(b));
    }

    [Fact]
    public async Task InitAsync_loads_once_and_ignores_later_database_changes()
    {
        if (!_fx.Available) return;
        var id = FreshCharId();
        var left = new DateTime(2026, 9, 20, 9, 0, 0, DateTimeKind.Utc);
        var repo = await StartAsync();
        await repo.InitAsync();                               

        await (await StartAsync()).RecordLeaveAsync(id, left); 

        await repo.InitAsync();                               
        Assert.Null(repo.LeftUtcOf(id));
        Assert.Equal(left, (await StartAsync()).LeftUtcOf(id));
    }

    [Fact]
    public async Task Concurrent_InitAsync_calls_all_complete()
    {
        if (!_fx.Available) return;
        var id = FreshCharId();
        var left = new DateTime(2026, 9, 20, 9, 30, 0, DateTimeKind.Utc);
        await (await StartAsync()).RecordLeaveAsync(id, left);

        var repo = new GuildDepartureRepository(_fx.Db!);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => repo.InitAsync()));

        Assert.Equal(left, repo.LeftUtcOf(id));
    }

    [Fact]
    public async Task Before_init_the_store_is_memory_only()
    {
        if (!_fx.Available) return;
        var id = FreshCharId();
        var left = new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);
        var repo = new GuildDepartureRepository(_fx.Db!);     

        Assert.False(await repo.TakeKickNoticeAsync(id));
        await repo.RecordLeaveAsync(id, left);
        await repo.MarkKickNoticeAsync(id);
        Assert.Equal(left, repo.LeftUtcOf(id));
        Assert.True(await repo.TakeKickNoticeAsync(id));
        Assert.False(await repo.TakeKickNoticeAsync(id));

        
        Assert.Null((await StartAsync()).LeftUtcOf(id));
    }

    [Fact]
    public async Task Failed_init_throws_and_leaves_the_store_memory_only()
    {
        if (!_fx.Available) return;
        var id = FreshCharId();
        var left = new DateTime(2026, 9, 20, 13, 0, 0, DateTimeKind.Utc);
        await using var broken = new PostgresDbContext(
            "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=2;Command Timeout=2");
        var repo = new GuildDepartureRepository(broken);

        await Assert.ThrowsAnyAsync<Exception>(() => repo.InitAsync());

        
        await repo.RecordLeaveAsync(id, left);
        await repo.MarkKickNoticeAsync(id);
        Assert.Equal(left, repo.LeftUtcOf(id));
        Assert.True(await repo.TakeKickNoticeAsync(id));
        Assert.Null((await StartAsync()).LeftUtcOf(id));
    }

    [Fact]
    public void KickNoticeBody_is_retails_constant()
    {
        Assert.Equal("050000000112000000", Convert.ToHexString(GuildDepartureRepository.KickNoticeBody()).ToLowerInvariant());
    }
}
