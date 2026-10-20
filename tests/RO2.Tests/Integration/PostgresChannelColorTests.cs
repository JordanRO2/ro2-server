using RO2.Persistence.Postgres.Repositories;
using Xunit;

namespace RO2.Tests.Integration;

/// <summary>
/// Runtime tests for <see cref="ChannelColorRepository"/> against a live PostgreSQL instance
/// (<see cref="PostgresFixture"/>). Covers empty-owner reads, set and get round-trips, in-place replacement,
/// multi-channel independence, owner isolation, and the uint-to-bigint range.
/// </summary>
/// <remarks>Each test uses a fresh random owner id so tests can share one container.</remarks>
[Collection("Postgres")]
public sealed class PostgresChannelColorTests
{
    private readonly PostgresFixture _fx;
    public PostgresChannelColorTests(PostgresFixture fx) => _fx = fx;

    private static uint NewOwner() => (uint)Random.Shared.NextInt64(1, uint.MaxValue);

    [Fact]
    public async Task Unknown_owner_has_no_colors()
    {
        if (!_fx.Available) return;
        var repo = new ChannelColorRepository(_fx.Db!);
        var colors = await repo.GetColorsAsync(NewOwner());
        Assert.NotNull(colors);
        Assert.Empty(colors);
    }

    [Fact]
    public async Task Set_then_get_round_trips_one_channel()
    {
        if (!_fx.Available) return;
        var repo = new ChannelColorRepository(_fx.Db!);
        var owner = NewOwner();
        await repo.SetColorAsync(owner, 12, 0xFF112233u);
        var colors = await repo.GetColorsAsync(owner);
        Assert.Single(colors);
        Assert.Equal(0xFF112233u, colors[12]);
    }

    [Fact]
    public async Task Set_replaces_existing_channel_value()
    {
        if (!_fx.Available) return;
        var repo = new ChannelColorRepository(_fx.Db!);
        var owner = NewOwner();
        await repo.SetColorAsync(owner, 13, 0xFF000001u);
        await repo.SetColorAsync(owner, 13, 0xFF000002u);
        var colors = await repo.GetColorsAsync(owner);
        Assert.Single(colors);
        Assert.Equal(0xFF000002u, colors[13]);
    }

    [Fact]
    public async Task Channels_are_independent_for_one_owner()
    {
        if (!_fx.Available) return;
        var repo = new ChannelColorRepository(_fx.Db!);
        var owner = NewOwner();
        await repo.SetColorAsync(owner, 12, 0xFFAA0000u);
        await repo.SetColorAsync(owner, 13, 0xFF00BB00u);
        await repo.SetColorAsync(owner, 14, 0xFF0000CCu);
        await repo.SetColorAsync(owner, 13, 0xFF123456u); 
        var colors = await repo.GetColorsAsync(owner);
        Assert.Equal(3, colors.Count);
        Assert.Equal(0xFFAA0000u, colors[12]);
        Assert.Equal(0xFF123456u, colors[13]);
        Assert.Equal(0xFF0000CCu, colors[14]);
    }

    [Fact]
    public async Task Owners_are_isolated()
    {
        if (!_fx.Available) return;
        var repo = new ChannelColorRepository(_fx.Db!);
        var a = NewOwner();
        var b = a == uint.MaxValue ? a - 1 : a + 1;
        await repo.SetColorAsync(a, 12, 0xFF111111u);
        await repo.SetColorAsync(b, 12, 0xFF222222u);
        await repo.SetColorAsync(b, 14, 0xFF333333u);
        var ca = await repo.GetColorsAsync(a);
        var cb = await repo.GetColorsAsync(b);
        Assert.Single(ca);
        Assert.Equal(0xFF111111u, ca[12]);
        Assert.Equal(2, cb.Count);
        Assert.Equal(0xFF222222u, cb[12]);
        Assert.Equal(0xFF333333u, cb[14]);
    }

    [Fact]
    public async Task Unsigned_extremes_round_trip()
    {
        if (!_fx.Available) return;
        var repo = new ChannelColorRepository(_fx.Db!);
        
        
        var owner = 4_000_000_000u + (uint)Random.Shared.Next(0, 200_000_000);
        await repo.SetColorAsync(owner, ushort.MaxValue, uint.MaxValue);
        await repo.SetColorAsync(owner, 0, 0u);
        var colors = await repo.GetColorsAsync(owner);
        Assert.Equal(2, colors.Count);
        Assert.Equal(uint.MaxValue, colors[ushort.MaxValue]);
        Assert.Equal(0u, colors[0]);
    }

    [Fact]
    public async Task Concurrent_sets_on_different_channels_all_persist()
    {
        if (!_fx.Available) return;
        var repo = new ChannelColorRepository(_fx.Db!);
        var owner = NewOwner();
        await Task.WhenAll(
            repo.SetColorAsync(owner, 12, 0xFF000012u),
            repo.SetColorAsync(owner, 13, 0xFF000013u),
            repo.SetColorAsync(owner, 14, 0xFF000014u));
        var colors = await repo.GetColorsAsync(owner);
        Assert.Equal(3, colors.Count);
        Assert.Equal(0xFF000012u, colors[12]);
        Assert.Equal(0xFF000013u, colors[13]);
        Assert.Equal(0xFF000014u, colors[14]);
    }
}
