using Npgsql;
using RO2.Application.Interfaces;
using RO2.Persistence.Postgres.Repositories;
using Xunit;

namespace RO2.Tests.Integration;

/// <summary>
/// Integration tests for <see cref="TimedEnchantRepository"/> against a live PostgreSQL instance.
/// </summary>
/// <remarks>
/// Covers the running-worn filter and the compare-and-set timer advance over the <c>inventory_items</c> table. Each test uses a fresh random identifier so tests can share one container.
/// </remarks>
[Collection("Postgres")]
public sealed class PostgresTimedEnchantTests
{
    private readonly PostgresFixture _fx;
    public PostgresTimedEnchantTests(PostgresFixture fx) => _fx = fx;

    private static long _keys = 3_500_000_000L + Random.Shared.Next(0, 100_000_000);
    private static uint NewChar() => (uint)Interlocked.Increment(ref _keys);

    private static long _itemIds = 0x5000_0000_0000_0000L + DateTime.UtcNow.Ticks % 1_000_000_000L;
    private static ulong NewItemId() => (ulong)Interlocked.Increment(ref _itemIds);

    private async Task SeedAsync(ulong id, uint characterId, short storageType, short slot, long enchantId, long seconds)
    {
        await using var conn = await _fx.Db!.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO inventory_items (id, character_id, item_id, slot, storage_type, enchant_reinforce_id, enchant_seconds) " +
            "VALUES (@id, @c, 5000, @slot, @st, @e, @s)", conn);
        cmd.Parameters.AddWithValue("id", (long)id);
        cmd.Parameters.AddWithValue("c", (long)characterId);
        cmd.Parameters.AddWithValue("slot", slot);
        cmd.Parameters.AddWithValue("st", storageType);
        cmd.Parameters.AddWithValue("e", enchantId);
        cmd.Parameters.AddWithValue("s", seconds);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<long> ReadSecondsAsync(ulong id)
    {
        await using var conn = await _fx.Db!.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("SELECT enchant_seconds FROM inventory_items WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", (long)id);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task GetRunningWorn_returns_only_worn_items_with_a_running_enchant()
    {
        if (!_fx.Available) return;
        var repo = new TimedEnchantRepository(_fx.Db!);
        var ch = NewChar();
        var other = NewChar();
        ulong running = NewItemId(), running2 = NewItemId(), inBag = NewItemId(), noEnchant = NewItemId(),
              noSeconds = NewItemId(), otherChar = NewItemId();
        await SeedAsync(running, ch, 1, 0, 700, 120);
        await SeedAsync(running2, ch, 1, 1, 701, 5);
        await SeedAsync(inBag, ch, 0, 0, 702, 90);       
        await SeedAsync(noEnchant, ch, 1, 2, 0, 90);     
        await SeedAsync(noSeconds, ch, 1, 3, 703, 0);    
        await SeedAsync(otherChar, other, 1, 0, 704, 60); 

        var result = await repo.GetRunningWornAsync(ch);

        Assert.Equal(2, result.Count);
        Assert.Equal(new RunningEnchant(running, 700, 120), Assert.Single(result, r => r.ItemId == running));
        Assert.Equal(new RunningEnchant(running2, 701, 5), Assert.Single(result, r => r.ItemId == running2));
        Assert.Empty(await repo.GetRunningWornAsync(NewChar()));
    }

    [Fact]
    public async Task TryAdvanceTimer_succeeds_on_an_exact_match_and_writes_only_the_seconds()
    {
        if (!_fx.Available) return;
        var repo = new TimedEnchantRepository(_fx.Db!);
        var ch = NewChar();
        var id = NewItemId();
        await SeedAsync(id, ch, 1, 0, 700, 120);

        Assert.True(await repo.TryAdvanceTimerAsync(id, 700, 120, 119));

        Assert.Equal(119, await ReadSecondsAsync(id));
        var after = Assert.Single(await repo.GetRunningWornAsync(ch));
        Assert.Equal(new RunningEnchant(id, 700, 119), after);
    }

    [Fact]
    public async Task TryAdvanceTimer_fails_without_change_when_the_expected_enchant_or_seconds_differ()
    {
        if (!_fx.Available) return;
        var repo = new TimedEnchantRepository(_fx.Db!);
        var id = NewItemId();
        await SeedAsync(id, NewChar(), 1, 0, 700, 120);

        Assert.False(await repo.TryAdvanceTimerAsync(id, 701, 120, 119)); 
        Assert.False(await repo.TryAdvanceTimerAsync(id, 700, 121, 119)); 
        Assert.False(await repo.TryAdvanceTimerAsync(NewItemId(), 700, 120, 119)); 
        Assert.Equal(120, await ReadSecondsAsync(id));

        Assert.True(await repo.TryAdvanceTimerAsync(id, 700, 120, 119));
        Assert.False(await repo.TryAdvanceTimerAsync(id, 700, 120, 118)); 
        Assert.Equal(119, await ReadSecondsAsync(id));
    }

    [Fact]
    public async Task TryAdvanceTimer_reports_false_when_the_value_would_not_change()
    {
        if (!_fx.Available) return;
        var repo = new TimedEnchantRepository(_fx.Db!);
        var id = NewItemId();
        await SeedAsync(id, NewChar(), 1, 0, 700, 120);

        Assert.False(await repo.TryAdvanceTimerAsync(id, 700, 120, 120)); 
        Assert.Equal(120, await ReadSecondsAsync(id));
    }

    [Fact]
    public async Task Ushort_values_round_trip_at_the_limits()
    {
        if (!_fx.Available) return;
        var repo = new TimedEnchantRepository(_fx.Db!);
        var ch = NewChar();
        var id = NewItemId();
        await SeedAsync(id, ch, 1, 0, ushort.MaxValue, ushort.MaxValue);

        var before = Assert.Single(await repo.GetRunningWornAsync(ch));
        Assert.Equal(new RunningEnchant(id, ushort.MaxValue, ushort.MaxValue), before);

        Assert.True(await repo.TryAdvanceTimerAsync(id, ushort.MaxValue, ushort.MaxValue, ushort.MaxValue - 1));
        Assert.Equal(ushort.MaxValue - 1, await ReadSecondsAsync(id));
        Assert.True(await repo.TryAdvanceTimerAsync(id, ushort.MaxValue, ushort.MaxValue - 1, 1));
        var after = Assert.Single(await repo.GetRunningWornAsync(ch));
        Assert.Equal(new RunningEnchant(id, ushort.MaxValue, 1), after);
    }
}
