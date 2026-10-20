using Npgsql;
using RO2.Persistence.Postgres.Repositories;
using Xunit;

namespace RO2.Tests.Integration;

/// <summary>
/// Tests the Postgres account access repository against a live database: the stored level is returned, a missing account yields 0, and id and level values round-trip at their limits.
/// </summary>
/// <remarks>
/// Each test seeds its own account row. Ids are random per test so the shared container does not collide.
/// </remarks>
[Collection("Postgres")]
public sealed class PostgresAccountAccessTests
{
    private readonly PostgresFixture _fx;
    public PostgresAccountAccessTests(PostgresFixture fx) => _fx = fx;

    private static uint NewId() => (uint)Random.Shared.NextInt64(1, uint.MaxValue);

    private async Task SeedAsync(uint id, int level)
    {
        await using var conn = await _fx.Db!.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO accounts (id, access_level) VALUES (@i, @l) " +
            "ON CONFLICT (id) DO UPDATE SET access_level = EXCLUDED.access_level", conn);
        cmd.Parameters.AddWithValue("i", (long)id);
        cmd.Parameters.AddWithValue("l", (short)level);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Returns_stored_access_level()
    {
        if (!_fx.Available) return;
        var repo = new AccountAccessRepository(_fx.Db!);
        var id = NewId();
        await SeedAsync(id, 5);
        Assert.Equal((byte)5, await repo.GetAccessLevelAsync(id));
    }

    [Fact]
    public async Task Unknown_account_is_zero()
    {
        if (!_fx.Available) return;
        var repo = new AccountAccessRepository(_fx.Db!);
        Assert.Equal((byte)0, await repo.GetAccessLevelAsync(NewId()));
    }

    [Fact]
    public async Task Default_level_row_is_zero()
    {
        if (!_fx.Available) return;
        var repo = new AccountAccessRepository(_fx.Db!);
        var id = NewId();
        await using (var conn = await _fx.Db!.OpenConnectionAsync())
        await using (var cmd = new NpgsqlCommand("INSERT INTO accounts (id) VALUES (@i)", conn))
        {
            cmd.Parameters.AddWithValue("i", (long)id);
            await cmd.ExecuteNonQueryAsync();
        }
        Assert.Equal((byte)0, await repo.GetAccessLevelAsync(id));
    }

    [Fact]
    public async Task Max_byte_level_and_large_uint_id_round_trip()
    {
        if (!_fx.Available) return;
        var repo = new AccountAccessRepository(_fx.Db!);
        
        var id = (uint)Random.Shared.NextInt64(4_000_000_000L, uint.MaxValue);
        await SeedAsync(id, 255);
        Assert.Equal((byte)255, await repo.GetAccessLevelAsync(id));
    }

    [Fact]
    public async Task Levels_are_isolated_per_account()
    {
        if (!_fx.Available) return;
        var repo = new AccountAccessRepository(_fx.Db!);
        var a = NewId();
        var b = a == uint.MaxValue ? a - 1 : a + 1;
        await SeedAsync(a, 1);
        await SeedAsync(b, 9);
        Assert.Equal((byte)1, await repo.GetAccessLevelAsync(a));
        Assert.Equal((byte)9, await repo.GetAccessLevelAsync(b));
    }

    [Fact]
    public async Task Reflects_level_changed_by_account_system()
    {
        if (!_fx.Available) return;
        var repo = new AccountAccessRepository(_fx.Db!);
        var id = NewId();
        await SeedAsync(id, 2);
        Assert.Equal((byte)2, await repo.GetAccessLevelAsync(id));
        await SeedAsync(id, 7); 
        Assert.Equal((byte)7, await repo.GetAccessLevelAsync(id));
    }
}
