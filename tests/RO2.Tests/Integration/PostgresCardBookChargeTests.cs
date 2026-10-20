using RO2.Persistence.Postgres.Repositories;
using Xunit;

namespace RO2.Tests.Integration;

/// <summary>
/// Runtime tests for <see cref="CardBookChargeRepository"/> against a live PostgreSQL instance
/// (<see cref="PostgresFixture"/>). Covers never-charged and lapsed states, extending from now or from a
/// running charge, restarting a lapsed charge, row retention, per-character isolation, and the uint-to-bigint
/// round-trip.
/// </summary>
/// <remarks>Each test uses a fresh random character id so tests can share one container.</remarks>
[Collection("Postgres")]
public sealed class PostgresCardBookChargeTests
{
    private readonly PostgresFixture _fx;
    public PostgresCardBookChargeTests(PostgresFixture fx) => _fx = fx;

    
    private static DateTime NowSeconds()
    {
        var t = DateTime.UtcNow;
        return new DateTime(t.Ticks - t.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
    }

    
    private static DateTime AddLocalDays(DateTime utc, int days) => utc.ToLocalTime().AddDays(days).ToUniversalTime();

    private static uint FreshId() => (uint)Random.Shared.NextInt64(1, uint.MaxValue);

    [Fact]
    public async Task Never_charged_is_null()
    {
        if (!_fx.Available) return;
        var repo = new CardBookChargeRepository(_fx.Db!);
        Assert.Null(await repo.GetChargedUntilUtcAsync(FreshId()));
    }

    [Fact]
    public async Task Extend_never_charged_starts_at_now()
    {
        if (!_fx.Available) return;
        var repo = new CardBookChargeRepository(_fx.Db!);
        uint id = FreshId();
        var now = NowSeconds();

        var until = await repo.ExtendChargeAsync(id, 30, now);

        Assert.Equal(AddLocalDays(now, 30), until);
        var stored = await repo.GetChargedUntilUtcAsync(id);
        Assert.NotNull(stored);
        Assert.Equal(until, stored!.Value);
        Assert.Equal(DateTimeKind.Utc, stored.Value.Kind);
    }

    [Fact]
    public async Task Extend_still_charged_extends_from_current_expiry()
    {
        if (!_fx.Available) return;
        var repo = new CardBookChargeRepository(_fx.Db!);
        uint id = FreshId();
        var now = NowSeconds();

        var first = await repo.ExtendChargeAsync(id, 30, now);
        
        var second = await repo.ExtendChargeAsync(id, 30, now.AddDays(1));

        Assert.Equal(AddLocalDays(first, 30), second);
        Assert.Equal(second, await repo.GetChargedUntilUtcAsync(id));
    }

    [Fact]
    public async Task Extend_lapsed_restarts_from_now_and_row_was_kept()
    {
        if (!_fx.Available) return;
        var repo = new CardBookChargeRepository(_fx.Db!);
        uint id = FreshId();
        var now = NowSeconds();

        
        var lapsed = await repo.ExtendChargeAsync(id, 30, now.AddDays(-100));
        Assert.True(lapsed < now);
        
        Assert.Equal(lapsed, await repo.GetChargedUntilUtcAsync(id));

        var renewed = await repo.ExtendChargeAsync(id, 30, now);

        Assert.Equal(AddLocalDays(now, 30), renewed);
        Assert.Equal(renewed, await repo.GetChargedUntilUtcAsync(id));
    }

    [Fact]
    public async Task Extend_exactly_at_expiry_counts_as_lapsed_and_starts_at_now()
    {
        if (!_fx.Available) return;
        var repo = new CardBookChargeRepository(_fx.Db!);
        uint id = FreshId();
        var now = NowSeconds();

        var first = await repo.ExtendChargeAsync(id, 10, now);
        
        var second = await repo.ExtendChargeAsync(id, 5, first);

        Assert.Equal(AddLocalDays(first, 5), second);
    }

    [Fact]
    public async Task Extend_zero_days_on_never_charged_stores_now()
    {
        if (!_fx.Available) return;
        var repo = new CardBookChargeRepository(_fx.Db!);
        uint id = FreshId();
        var now = NowSeconds();

        var until = await repo.ExtendChargeAsync(id, 0, now);

        Assert.Equal(now, until);
        Assert.Equal(now, await repo.GetChargedUntilUtcAsync(id));
    }

    [Fact]
    public async Task Characters_are_independent()
    {
        if (!_fx.Available) return;
        var repo = new CardBookChargeRepository(_fx.Db!);
        uint a = FreshId();
        uint b = a == uint.MaxValue ? a - 1 : a + 1;
        var now = NowSeconds();

        var untilA = await repo.ExtendChargeAsync(a, 30, now);

        Assert.Equal(untilA, await repo.GetChargedUntilUtcAsync(a));
        Assert.Null(await repo.GetChargedUntilUtcAsync(b));
    }

    [Fact]
    public async Task Character_id_above_int_max_round_trips()
    {
        if (!_fx.Available) return;
        var repo = new CardBookChargeRepository(_fx.Db!);
        
        uint id = (uint)Random.Shared.NextInt64(3_000_000_000, uint.MaxValue);
        var now = NowSeconds();

        var until = await repo.ExtendChargeAsync(id, 7, now);

        Assert.Equal(AddLocalDays(now, 7), until);
        Assert.Equal(until, await repo.GetChargedUntilUtcAsync(id));
    }
}
