using Npgsql;
using RO2.Persistence.Postgres.Repositories;
using Xunit;

namespace RO2.Tests.Integration;

/// <summary>
/// Runtime tests for <see cref="GuildStorageLogRepository"/> against a live PostgreSQL instance
/// (<see cref="PostgresFixture"/>). Covers item and funds log lines, newest-first paging with totals,
/// day-window filtering, guild isolation, null-actor coalescing, and the empty-page path.
/// </summary>
/// <remarks>Each test uses a fresh random 16-byte guild GUID so tests can share one container.</remarks>
[Collection("Postgres")]
public sealed class PostgresGuildStorageLogTests
{
    private readonly PostgresFixture _fx;
    public PostgresGuildStorageLogTests(PostgresFixture fx) => _fx = fx;

    private static byte[] NewGuild() => Guid.NewGuid().ToByteArray();

 /// <summary>Backdate every line of a guild so the day window can be exercised.</summary>
    private async Task BackdateAsync(string table, byte[] guid, DateTime when, long itemIdOrAmountFilter = -1)
    {
        await using var conn = await _fx.Db!.OpenConnectionAsync();
        var col = table == GuildStorageLogRepository.ItemTableName ? "item_id" : "amount";
        await using var cmd = new NpgsqlCommand(
            $"UPDATE {table} SET when_utc = @w WHERE guild_guid = @g AND (@f = -1 OR {col} = @f)", conn);
        cmd.Parameters.AddWithValue("w", when);
        cmd.Parameters.AddWithValue("g", guid);
        cmd.Parameters.AddWithValue("f", itemIdOrAmountFilter);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Item_line_round_trips_all_fields()
    {
        if (!_fx.Available) return;
        var repo = new GuildStorageLogRepository(_fx.Db!);
        var guild = NewGuild();
        var before = DateTime.UtcNow.AddSeconds(-5);
        
        await repo.AddAsync(guild, "Alice", 255, 4_000_000_000u, 7);

        var (total, lines) = await repo.GetPageAsync(guild, 0, 0, 10);
        Assert.Equal(1, total);
        var l = Assert.Single(lines);
        Assert.Equal("Alice", l.ActorName);
        Assert.Equal((byte)255, l.Kind);
        Assert.Equal(4_000_000_000u, l.ItemId);
        Assert.Equal(7, l.Count);
        Assert.Equal(DateTimeKind.Utc, l.WhenUtc.Kind);
        Assert.InRange(l.WhenUtc, before, DateTime.UtcNow.AddSeconds(5));
    }

    [Fact]
    public async Task Funds_line_round_trips_all_fields()
    {
        if (!_fx.Available) return;
        var repo = new GuildStorageLogRepository(_fx.Db!);
        var guild = NewGuild();
        var before = DateTime.UtcNow.AddSeconds(-5);
        await repo.AddFundsAsync(guild, "Bob", 1, 9_000_000_000L);

        var (total, lines) = await repo.GetFundsPageAsync(guild, 0, 0, 10);
        Assert.Equal(1, total);
        var l = Assert.Single(lines);
        Assert.Equal("Bob", l.ActorName);
        Assert.Equal((byte)1, l.Kind);
        Assert.Equal(9_000_000_000L, l.Amount);
        Assert.Equal(DateTimeKind.Utc, l.WhenUtc.Kind);
        Assert.InRange(l.WhenUtc, before, DateTime.UtcNow.AddSeconds(5));
    }

    [Fact]
    public async Task Unknown_guild_is_empty_page_with_zero_total()
    {
        if (!_fx.Available) return;
        var repo = new GuildStorageLogRepository(_fx.Db!);
        var guild = NewGuild();

        var (itemTotal, itemLines) = await repo.GetPageAsync(guild, 0, 0, 10);
        Assert.Equal(0, itemTotal);
        Assert.Empty(itemLines);

        var (fundsTotal, fundsLines) = await repo.GetFundsPageAsync(guild, 0, 0, 10);
        Assert.Equal(0, fundsTotal);
        Assert.Empty(fundsLines);
    }

    [Fact]
    public async Task Item_pages_newest_first_with_total_over_whole_window()
    {
        if (!_fx.Available) return;
        var repo = new GuildStorageLogRepository(_fx.Db!);
        var guild = NewGuild();
        for (uint i = 1; i <= 5; i++)
            await repo.AddAsync(guild, "A" + i, 0, i, (int)i);

        var (total0, page0) = await repo.GetPageAsync(guild, 0, 0, 2);
        Assert.Equal(5, total0);
        Assert.Equal(new uint[] { 5, 4 }, page0.Select(x => x.ItemId).ToArray());

        var (total1, page1) = await repo.GetPageAsync(guild, 1, 0, 2);
        Assert.Equal(5, total1);
        Assert.Equal(new uint[] { 3, 2 }, page1.Select(x => x.ItemId).ToArray());

        var (total2, page2) = await repo.GetPageAsync(guild, 2, 0, 2);
        Assert.Equal(5, total2);
        Assert.Equal(new uint[] { 1 }, page2.Select(x => x.ItemId).ToArray());

        var (total3, page3) = await repo.GetPageAsync(guild, 3, 0, 2);
        Assert.Equal(5, total3);
        Assert.Empty(page3); 
    }

    [Fact]
    public async Task Funds_pages_newest_first_with_total_over_whole_window()
    {
        if (!_fx.Available) return;
        var repo = new GuildStorageLogRepository(_fx.Db!);
        var guild = NewGuild();
        for (long i = 1; i <= 5; i++)
            await repo.AddFundsAsync(guild, "F" + i, (byte)(i % 2), i * 1000);

        var (total0, page0) = await repo.GetFundsPageAsync(guild, 0, 0, 2);
        Assert.Equal(5, total0);
        Assert.Equal(new long[] { 5000, 4000 }, page0.Select(x => x.Amount).ToArray());

        var (total1, page1) = await repo.GetFundsPageAsync(guild, 1, 0, 2);
        Assert.Equal(5, total1);
        Assert.Equal(new long[] { 3000, 2000 }, page1.Select(x => x.Amount).ToArray());

        var (total2, page2) = await repo.GetFundsPageAsync(guild, 2, 0, 2);
        Assert.Equal(5, total2);
        Assert.Equal(new long[] { 1000 }, page2.Select(x => x.Amount).ToArray());

        var (_, page3) = await repo.GetFundsPageAsync(guild, 3, 0, 2);
        Assert.Empty(page3);
    }

    [Fact]
    public async Task Item_day_window_filters_old_lines_and_zero_days_means_all()
    {
        if (!_fx.Available) return;
        var repo = new GuildStorageLogRepository(_fx.Db!);
        var guild = NewGuild();
        await repo.AddAsync(guild, "Old", 0, 100, 1);
        await repo.AddAsync(guild, "Mid", 0, 200, 1);
        await repo.AddAsync(guild, "New", 0, 300, 1);
        await BackdateAsync(GuildStorageLogRepository.ItemTableName, guild, DateTime.UtcNow.AddDays(-10), 100);
        await BackdateAsync(GuildStorageLogRepository.ItemTableName, guild, DateTime.UtcNow.AddDays(-3), 200);

        var (all, allLines) = await repo.GetPageAsync(guild, 0, 0, 10);
        Assert.Equal(3, all);
        Assert.Equal(new uint[] { 300, 200, 100 }, allLines.Select(x => x.ItemId).ToArray());

        var (d5, d5Lines) = await repo.GetPageAsync(guild, 0, 5, 10);
        Assert.Equal(2, d5);
        Assert.Equal(new uint[] { 300, 200 }, d5Lines.Select(x => x.ItemId).ToArray());

        var (d1, d1Lines) = await repo.GetPageAsync(guild, 0, 1, 10);
        Assert.Equal(1, d1);
        Assert.Equal(new uint[] { 300 }, d1Lines.Select(x => x.ItemId).ToArray());
    }

    [Fact]
    public async Task Funds_day_window_filters_old_lines_and_zero_days_means_all()
    {
        if (!_fx.Available) return;
        var repo = new GuildStorageLogRepository(_fx.Db!);
        var guild = NewGuild();
        await repo.AddFundsAsync(guild, "Old", 0, 100);
        await repo.AddFundsAsync(guild, "Mid", 1, 200);
        await repo.AddFundsAsync(guild, "New", 0, 300);
        await BackdateAsync(GuildStorageLogRepository.FundsTableName, guild, DateTime.UtcNow.AddDays(-10), 100);
        await BackdateAsync(GuildStorageLogRepository.FundsTableName, guild, DateTime.UtcNow.AddDays(-3), 200);

        var (all, allLines) = await repo.GetFundsPageAsync(guild, 0, 0, 10);
        Assert.Equal(3, all);
        Assert.Equal(new long[] { 300, 200, 100 }, allLines.Select(x => x.Amount).ToArray());

        var (d5, d5Lines) = await repo.GetFundsPageAsync(guild, 0, 5, 10);
        Assert.Equal(2, d5);
        Assert.Equal(new long[] { 300, 200 }, d5Lines.Select(x => x.Amount).ToArray());

        var (d1, d1Lines) = await repo.GetFundsPageAsync(guild, 0, 1, 10);
        Assert.Equal(1, d1);
        Assert.Equal(new long[] { 300 }, d1Lines.Select(x => x.Amount).ToArray());
    }

    [Fact]
    public async Task Window_orders_by_time_not_insertion()
    {
        if (!_fx.Available) return;
        var repo = new GuildStorageLogRepository(_fx.Db!);
        var guild = NewGuild();
        await repo.AddAsync(guild, "First", 0, 1, 1);
        await repo.AddAsync(guild, "Second", 0, 2, 1);
        
        await BackdateAsync(GuildStorageLogRepository.ItemTableName, guild, DateTime.UtcNow.AddHours(-2), 2);

        var (_, lines) = await repo.GetPageAsync(guild, 0, 0, 10);
        Assert.Equal(new uint[] { 1, 2 }, lines.Select(x => x.ItemId).ToArray());
    }

    [Fact]
    public async Task Guilds_are_isolated_and_item_and_funds_histories_are_separate()
    {
        if (!_fx.Available) return;
        var repo = new GuildStorageLogRepository(_fx.Db!);
        var a = NewGuild();
        var b = NewGuild();
        await repo.AddAsync(a, "InA", 0, 11, 1);
        await repo.AddAsync(b, "InB", 0, 22, 1);
        await repo.AddFundsAsync(a, "FundsA", 0, 5);

        var (aTotal, aLines) = await repo.GetPageAsync(a, 0, 0, 10);
        Assert.Equal(1, aTotal);
        Assert.Equal("InA", Assert.Single(aLines).ActorName);

        var (bTotal, bLines) = await repo.GetPageAsync(b, 0, 0, 10);
        Assert.Equal(1, bTotal);
        Assert.Equal("InB", Assert.Single(bLines).ActorName);

        var (aFundsTotal, aFunds) = await repo.GetFundsPageAsync(a, 0, 0, 10);
        Assert.Equal(1, aFundsTotal);
        Assert.Equal("FundsA", Assert.Single(aFunds).ActorName);

        var (bFundsTotal, bFunds) = await repo.GetFundsPageAsync(b, 0, 0, 10);
        Assert.Equal(0, bFundsTotal);
        Assert.Empty(bFunds);
    }

    [Fact]
    public async Task Guid_identity_is_the_full_16_bytes()
    {
        if (!_fx.Available) return;
        var repo = new GuildStorageLogRepository(_fx.Db!);
        var a = NewGuild();
        var b = (byte[])a.Clone();
        b[15] ^= 0xFF; 
        await repo.AddAsync(a, "A", 0, 1, 1);

        Assert.Equal(1, (await repo.GetPageAsync(a, 0, 0, 10)).Total);
        Assert.Equal(0, (await repo.GetPageAsync(b, 0, 0, 10)).Total);
    }

    [Fact]
    public async Task Null_actor_name_is_stored_as_empty_string()
    {
        if (!_fx.Available) return;
        var repo = new GuildStorageLogRepository(_fx.Db!);
        var guild = NewGuild();
        await repo.AddAsync(guild, null!, 0, 1, 1);
        await repo.AddFundsAsync(guild, null!, 1, 1);

        Assert.Equal(string.Empty, Assert.Single((await repo.GetPageAsync(guild, 0, 0, 10)).Lines).ActorName);
        Assert.Equal(string.Empty, Assert.Single((await repo.GetFundsPageAsync(guild, 0, 0, 10)).Lines).ActorName);
    }

    [Fact]
    public async Task Page_size_below_one_is_clamped_to_one_and_negative_page_to_zero()
    {
        if (!_fx.Available) return;
        var repo = new GuildStorageLogRepository(_fx.Db!);
        var guild = NewGuild();
        await repo.AddAsync(guild, "A", 0, 1, 1);
        await repo.AddAsync(guild, "B", 0, 2, 1);
        await repo.AddFundsAsync(guild, "A", 0, 1);
        await repo.AddFundsAsync(guild, "B", 0, 2);

        var (total, lines) = await repo.GetPageAsync(guild, -3, 0, 0);
        Assert.Equal(2, total);
        Assert.Equal(new uint[] { 2 }, lines.Select(x => x.ItemId).ToArray());

        var (fundsTotal, fundsLines) = await repo.GetFundsPageAsync(guild, -3, 0, -5);
        Assert.Equal(2, fundsTotal);
        Assert.Equal(new long[] { 2 }, fundsLines.Select(x => x.Amount).ToArray());
    }

    [Fact]
    public async Task Failures_are_swallowed_best_effort()
    {
        if (!_fx.Available) return;
        var repo = new GuildStorageLogRepository(_fx.Db!);
        var guild = NewGuild();
        
        
        var (total, lines) = await repo.GetPageAsync(guild, int.MaxValue, 0, int.MaxValue);
        Assert.Equal(0, total);
        Assert.Empty(lines);

        var (fundsTotal, fundsLines) = await repo.GetFundsPageAsync(guild, 0, int.MaxValue, 10); 
        Assert.Equal(0, fundsTotal);
        Assert.Empty(fundsLines);

        
        await repo.AddAsync(null!, "X", 0, 1, 1);
        await repo.AddFundsAsync(null!, "X", 0, 1);
    }
}
