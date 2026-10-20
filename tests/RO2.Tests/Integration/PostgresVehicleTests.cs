using RO2.Domain.Entities;
using RO2.Persistence.Postgres.Repositories;
using Xunit;

namespace RO2.Tests.Integration;

/// <summary>
/// Integration tests for <see cref="VehicleRepository"/> against a live PostgreSQL instance.
/// </summary>
/// <remarks>
/// Covers the empty read, grant (new, extend, permanent), timed extension, second truncation, double round-trips, stored order and concurrent grants. Each test uses a fresh random identifier so tests can share one container.
/// </remarks>
[Collection("Postgres")]
public sealed class PostgresVehicleTests
{
    private const double Now = 46223.5720; 

    
    private const double NowHalfSecond = 46223.5724;

    private readonly PostgresFixture _fx;
    public PostgresVehicleTests(PostgresFixture fx) => _fx = fx;

    
    private static uint NewCharacterId() => (uint)Random.Shared.NextInt64(2_200_000_000L, 4_200_000_000L);

    [Fact]
    public async Task Get_with_nothing_stored_is_empty()
    {
        if (!_fx.Available) return;
        var repo = new VehicleRepository(_fx.Db!);
        Assert.Empty(await repo.GetAsync(NewCharacterId()));
    }

    [Fact]
    public async Task Grant_new_timed_vehicle_adds_now_plus_days_as_float32_and_persists()
    {
        if (!_fx.Available) return;
        var repo = new VehicleRepository(_fx.Db!);
        var id = NewCharacterId();

        var result = await repo.GrantAsync(id, 90190000, 5, Now);

        var v = Assert.Single(result);
        Assert.Equal(90190000u, v.RidingId);
        Assert.Equal((double)(float)(Now + 5), v.ExpiryOle);

        var stored = Assert.Single(await repo.GetAsync(id));
        Assert.Equal(90190000u, stored.RidingId);
        Assert.Equal(v.ExpiryOle, stored.ExpiryOle); 
    }

    [Fact]
    public async Task Grant_adds_to_current_expiry_when_not_expired_and_to_now_when_expired()
    {
        if (!_fx.Available) return;
        var repo = new VehicleRepository(_fx.Db!);
        var id = NewCharacterId();

        var first = Assert.Single(await repo.GrantAsync(id, 90190000, 5, Now));
        var second = Assert.Single(await repo.GrantAsync(id, 90190000, 5, Now)); 
        Assert.Equal((double)(float)(first.ExpiryOle + 5), second.ExpiryOle);

        var later = second.ExpiryOle + 100; 
        var third = Assert.Single(await repo.GrantAsync(id, 90190000, 5, later));
        Assert.Equal((double)(float)(later + 5), third.ExpiryOle);
    }

    [Fact]
    public async Task Grant_permanent_and_timed_on_permanent_stay_permanent()
    {
        if (!_fx.Available) return;
        var repo = new VehicleRepository(_fx.Db!);
        var id = NewCharacterId();

        var timed = Assert.Single(await repo.GrantAsync(id, 90220000, 14, Now));
        Assert.NotEqual(0d, timed.ExpiryOle);

        var perm = Assert.Single(await repo.GrantAsync(id, 90220000, null, Now)); 
        Assert.Equal(0d, perm.ExpiryOle);

        var still = Assert.Single(await repo.GrantAsync(id, 90220000, 14, Now)); 
        Assert.Equal(0d, still.ExpiryOle);
        Assert.Equal(0d, Assert.Single(await repo.GetAsync(id)).ExpiryOle);
    }

    [Fact]
    public async Task Grant_returns_list_sorted_by_riding_id_and_keeps_others()
    {
        if (!_fx.Available) return;
        var repo = new VehicleRepository(_fx.Db!);
        var id = NewCharacterId();

        await repo.GrantAsync(id, 4_000_000_001, null, Now); 
        await repo.GrantAsync(id, 90280000, 14, Now);
        var result = await repo.GrantAsync(id, 90150000, null, Now);

        Assert.Equal(new[] { 90150000u, 90280000u, 4_000_000_001u }, result.Select(v => v.RidingId));
        Assert.Equal(new[] { 90150000u, 90280000u, 4_000_000_001u }, (await repo.GetAsync(id)).Select(v => v.RidingId));
        Assert.Equal(0d, result[0].ExpiryOle);
        Assert.NotEqual(0d, result[1].ExpiryOle);
        Assert.Equal(0d, result[2].ExpiryOle);
    }

    [Fact]
    public async Task Characters_are_independent()
    {
        if (!_fx.Available) return;
        var repo = new VehicleRepository(_fx.Db!);
        var a = NewCharacterId();
        var b = a == uint.MaxValue ? a - 1 : a + 1;

        await repo.GrantAsync(a, 90190000, null, Now);

        Assert.Single(await repo.GetAsync(a));
        Assert.Empty(await repo.GetAsync(b));
    }

    [Fact]
    public async Task TryExtendTimed_null_when_not_owned_and_stores_nothing()
    {
        if (!_fx.Available) return;
        var repo = new VehicleRepository(_fx.Db!);
        var id = NewCharacterId();

        Assert.Null(await repo.TryExtendTimedAsync(id, 90190000, 5, Now));
        Assert.Empty(await repo.GetAsync(id));

        await repo.GrantAsync(id, 90280000, 14, Now);
        Assert.Null(await repo.TryExtendTimedAsync(id, 90190000, 5, Now)); 
        Assert.Equal(90280000u, Assert.Single(await repo.GetAsync(id)).RidingId);
    }

    [Fact]
    public async Task TryExtendTimed_null_when_permanent_and_leaves_it_untouched()
    {
        if (!_fx.Available) return;
        var repo = new VehicleRepository(_fx.Db!);
        var id = NewCharacterId();

        await repo.GrantAsync(id, 90190000, null, Now);
        Assert.Null(await repo.TryExtendTimedAsync(id, 90190000, 5, Now));

        Assert.Equal(0d, Assert.Single(await repo.GetAsync(id)).ExpiryOle);
    }

    [Fact]
    public async Task TryExtendTimed_extends_a_timed_vehicle_and_keeps_the_others()
    {
        if (!_fx.Available) return;
        var repo = new VehicleRepository(_fx.Db!);
        var id = NewCharacterId();

        var granted = await repo.GrantAsync(id, 90190000, 5, Now);
        await repo.GrantAsync(id, 90150000, null, Now);

        var extended = await repo.TryExtendTimedAsync(id, 90190000, 5, Now);

        Assert.NotNull(extended);
        Assert.Equal(new[] { 90150000u, 90190000u }, extended!.Select(v => v.RidingId));
        Assert.Equal(0d, extended[0].ExpiryOle);
        Assert.Equal((double)(float)(granted[0].ExpiryOle + 5), extended[1].ExpiryOle);

        var stored = await repo.GetAsync(id);
        Assert.Equal(extended[1].ExpiryOle, stored.Single(v => v.RidingId == 90190000).ExpiryOle);
    }

    [Fact]
    public async Task TryExtendTimed_on_an_expired_vehicle_counts_from_now()
    {
        if (!_fx.Available) return;
        var repo = new VehicleRepository(_fx.Db!);
        var id = NewCharacterId();

        var granted = Assert.Single(await repo.GrantAsync(id, 90190000, 5, Now));
        var later = granted.ExpiryOle + 50; 

        var extended = await repo.TryExtendTimedAsync(id, 90190000, 3, later);

        Assert.Equal((double)(float)(later + 3), Assert.Single(extended!).ExpiryOle);
    }

    [Fact]
    public async Task LoadAndCut_with_nothing_stored_is_empty_and_writes_nothing()
    {
        if (!_fx.Available) return;
        var repo = new VehicleRepository(_fx.Db!);
        var id = NewCharacterId();

        Assert.Empty(await repo.LoadAndCutToWholeSecondsAsync(id));
        Assert.Empty(await repo.GetAsync(id));
        Assert.Equal(0L, await CountRowsAsync(id)); 
    }

    [Fact]
    public async Task LoadAndCut_cuts_to_whole_seconds_and_stores_the_cut_list()
    {
        if (!_fx.Available) return;
        var repo = new VehicleRepository(_fx.Db!);
        var id = NewCharacterId();

        
        var granted = await repo.GrantAsync(id, 90190000, 5, NowHalfSecond);
        await repo.GrantAsync(id, 90150000, null, NowHalfSecond);
        var timedExpiry = granted[0].ExpiryOle;
        var expectedCut = VehicleRules.WholeSecondExpiry(timedExpiry);
        Assert.NotEqual(timedExpiry, expectedCut);

        var cut = await repo.LoadAndCutToWholeSecondsAsync(id);

        Assert.Equal(new[] { 90150000u, 90190000u }, cut.Select(v => v.RidingId));
        Assert.Equal(0d, cut[0].ExpiryOle); 
        Assert.Equal(expectedCut, cut[1].ExpiryOle);

        
        var stored = await repo.GetAsync(id);
        Assert.Equal(expectedCut, stored.Single(v => v.RidingId == 90190000).ExpiryOle);

        
        var again = await repo.LoadAndCutToWholeSecondsAsync(id);
        Assert.Equal(expectedCut, again.Single(v => v.RidingId == 90190000).ExpiryOle);
    }

    [Fact]
    public async Task LoadAndCut_leaves_whole_second_expiries_bit_for_bit()
    {
        if (!_fx.Available) return;
        var repo = new VehicleRepository(_fx.Db!);
        var id = NewCharacterId();

        
        var wholeSecond = new DateTime(2026, 7, 21, 8, 20, 37, DateTimeKind.Unspecified).ToOADate();
        await SeedAsync(id, $"[{{\"RidingId\":90220000,\"ExpiryOle\":{wholeSecond.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}}}]");

        var result = await repo.LoadAndCutToWholeSecondsAsync(id);

        var v = Assert.Single(result);
        Assert.Equal(BitConverter.DoubleToInt64Bits(wholeSecond), BitConverter.DoubleToInt64Bits(v.ExpiryOle));
        Assert.Equal(BitConverter.DoubleToInt64Bits(wholeSecond),
            BitConverter.DoubleToInt64Bits(Assert.Single(await repo.GetAsync(id)).ExpiryOle));
    }

    [Fact]
    public async Task Get_returns_the_stored_order_unsorted()
    {
        if (!_fx.Available) return;
        var repo = new VehicleRepository(_fx.Db!);
        var id = NewCharacterId();

        await SeedAsync(id, "[{\"RidingId\":90280000,\"ExpiryOle\":0},{\"RidingId\":90150000,\"ExpiryOle\":46000.5}]");

        var stored = await repo.GetAsync(id);

        Assert.Equal(new[] { 90280000u, 90150000u }, stored.Select(v => v.RidingId));
        Assert.Equal(46000.5, stored[1].ExpiryOle);
    }

    [Fact]
    public async Task Concurrent_grants_for_different_vehicles_all_land()
    {
        if (!_fx.Available) return;
        var repo = new VehicleRepository(_fx.Db!);
        var id = NewCharacterId();

        
        
        var ids = new uint[] { 90190000, 90220000, 90280000 };
        await Task.WhenAll(ids.Select(r => Task.Run(() => repo.GrantAsync(id, r, null, Now))));

        var stored = await repo.GetAsync(id);
        Assert.Equal(ids, stored.Select(v => v.RidingId));
    }

    [Fact]
    public async Task A_concurrent_cut_never_loses_a_grant()
    {
        if (!_fx.Available) return;
        var repo = new VehicleRepository(_fx.Db!);
        var id = NewCharacterId();
        await repo.GrantAsync(id, 90190000, 5, NowHalfSecond); 

        
        await Task.WhenAll(
            Task.Run(() => repo.LoadAndCutToWholeSecondsAsync(id)),
            Task.Run(() => repo.GrantAsync(id, 90280000, null, Now)));

        var stored = await repo.GetAsync(id);
        Assert.Equal(new[] { 90190000u, 90280000u }, stored.Select(v => v.RidingId));
    }

    private async Task<long> CountRowsAsync(uint characterId)
    {
        await using var conn = await _fx.Db!.OpenConnectionAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(
            $"SELECT count(*) FROM {VehicleRepository.TableName} WHERE character_id = @id", conn);
        cmd.Parameters.AddWithValue("id", (long)characterId);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task SeedAsync(uint characterId, string vehiclesJson)
    {
        await using var conn = await _fx.Db!.OpenConnectionAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(
            $"INSERT INTO {VehicleRepository.TableName} (character_id, vehicles, updated_at) VALUES (@id, @v, @t)", conn);
        cmd.Parameters.AddWithValue("id", (long)characterId);
        cmd.Parameters.AddWithValue("v", NpgsqlTypes.NpgsqlDbType.Jsonb, vehiclesJson);
        cmd.Parameters.AddWithValue("t", DateTime.UtcNow);
        await cmd.ExecuteNonQueryAsync();
    }
}
