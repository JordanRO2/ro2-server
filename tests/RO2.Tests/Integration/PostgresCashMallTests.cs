using Npgsql;
using RO2.Domain.Entities;
using RO2.Persistence.Postgres.Repositories;
using Xunit;

namespace RO2.Tests.Integration;

/// <summary>
/// Runtime tests for <see cref="CashMallRepository"/> against a live PostgreSQL instance
/// (<see cref="PostgresFixture"/>). Covers balances, box reads, purchase history, id sequences, the
/// conditional debit (<c>balance &gt;= total</c>), refunds, gift filing, and unit take and return compare-and-set.
/// </summary>
/// <remarks>
/// Each test uses a fresh random account or counter name so tests can share one container. Balances are
/// seeded with raw SQL because the repository has no grant method.
/// </remarks>
[Collection("Postgres")]
public sealed class PostgresCashMallTests
{
    private static readonly DateTime Day = new(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);

    private readonly PostgresFixture _fx;
    public PostgresCashMallTests(PostgresFixture fx) => _fx = fx;

    private static uint NewAccount() => (uint)Random.Shared.NextInt64(1, uint.MaxValue);

    private static CashMallBoxEntry Entry(uint id, byte box = 1, uint item = 70004012, int count = 1,
        DateTime? filed = null, string sender = "") => new()
    {
        EntryId = id, Box = box, ItemId = item, Count = count, FiledUtc = filed ?? Day, SenderName = sender,
    };

    private static CashMallPurchase Purchase(uint order, uint item = 70004012, long unitPrice = 30) => new()
    {
        OrderId = order, ItemId = item, ProductId = 9001, Quantity = 2, UnitPrice = unitPrice, BoughtUtc = Day,
    };

    private async Task SeedBalanceAsync(uint account, long balance)
    {
        await using var conn = await _fx.Db!.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO cash_mall_accounts (account_id, balance) VALUES (@a, @b) " +
            "ON CONFLICT (account_id) DO UPDATE SET balance = EXCLUDED.balance", conn);
        cmd.Parameters.AddWithValue("a", (long)account);
        cmd.Parameters.AddWithValue("b", balance);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<long> AccountRowsAsync(uint account)
    {
        await using var conn = await _fx.Db!.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM cash_mall_accounts WHERE account_id = @a", conn);
        cmd.Parameters.AddWithValue("a", (long)account);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task Unknown_account_reads_as_empty()
    {
        if (!_fx.Available) return;
        var repo = new CashMallRepository(_fx.Db!);
        var account = NewAccount();
        Assert.Equal(0L, await repo.GetBalanceAsync(account));
        Assert.Empty(await repo.GetBoxAsync(account, 1));
        Assert.Empty(await repo.GetBoxAsync(account, 2));
        Assert.Null(await repo.GetBoxEntryAsync(account, 1));
        Assert.Equal((0, 0), await repo.GetBoxCountsAsync(account));
        Assert.Empty(await repo.GetPurchaseHistoryAsync(account));
    }

    [Fact]
    public async Task Balance_round_trips_beyond_int_range()
    {
        if (!_fx.Available) return;
        var repo = new CashMallRepository(_fx.Db!);
        var account = NewAccount();
        await SeedBalanceAsync(account, 5_000_000_000L);
        Assert.Equal(5_000_000_000L, await repo.GetBalanceAsync(account));
    }

    [Fact]
    public async Task ReserveIds_are_consecutive_from_one_and_continue()
    {
        if (!_fx.Available) return;
        var repo = new CashMallRepository(_fx.Db!);
        var counter = "test-" + Guid.NewGuid();
        Assert.Equal(new uint[] { 1, 2, 3 }, await repo.ReserveIdsAsync(counter, 3));
        Assert.Equal(new uint[] { 4, 5 }, await repo.ReserveIdsAsync(counter, 2));
        Assert.Equal(new uint[] { 6 }, await repo.ReserveIdsAsync(counter, 1));
    }

    [Fact]
    public async Task ReserveIds_non_positive_count_is_empty_and_does_not_advance()
    {
        if (!_fx.Available) return;
        var repo = new CashMallRepository(_fx.Db!);
        var counter = "test-" + Guid.NewGuid();
        Assert.Empty(await repo.ReserveIdsAsync(counter, 0));
        Assert.Empty(await repo.ReserveIdsAsync(counter, -4));
        Assert.Equal(new uint[] { 1 }, await repo.ReserveIdsAsync(counter, 1));
    }

    [Fact]
    public async Task ReserveIds_counters_are_independent()
    {
        if (!_fx.Available) return;
        var repo = new CashMallRepository(_fx.Db!);
        var a = "test-" + Guid.NewGuid();
        var b = "test-" + Guid.NewGuid();
        await repo.ReserveIdsAsync(a, 10);
        Assert.Equal(new uint[] { 1, 2 }, await repo.ReserveIdsAsync(b, 2));
        Assert.Equal(new uint[] { 11 }, await repo.ReserveIdsAsync(a, 1));
    }

    [Fact]
    public async Task ReserveIds_concurrent_calls_never_overlap()
    {
        if (!_fx.Available) return;
        var repo = new CashMallRepository(_fx.Db!);
        var counter = "test-" + Guid.NewGuid();
        var batches = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => repo.ReserveIdsAsync(counter, 5)));
        var all = batches.SelectMany(b => b).OrderBy(i => i).ToArray();
        Assert.Equal(Enumerable.Range(1, 50).Select(i => (uint)i).ToArray(), all);
    }

    [Fact]
    public async Task TryPurchase_debits_logs_and_files_entries()
    {
        if (!_fx.Available) return;
        var repo = new CashMallRepository(_fx.Db!);
        var account = NewAccount();
        await SeedBalanceAsync(account, 100);

        var after = await repo.TryPurchaseAsync(account, 60,
            new[] { Purchase(11), Purchase(12, item: 70004013) },
            new[] { Entry(21, box: 1, count: 2) });

        Assert.Equal((long?)40, after);
        Assert.Equal(40L, await repo.GetBalanceAsync(account));
        var history = await repo.GetPurchaseHistoryAsync(account);
        Assert.Equal(new uint[] { 11, 12 }, history.Select(p => p.OrderId).ToArray());
        var entry = Assert.Single(await repo.GetBoxAsync(account, 1));
        Assert.Equal(21u, entry.EntryId);
        Assert.Equal(2, entry.Count);
    }

    [Fact]
    public async Task TryPurchase_insufficient_balance_is_null_and_changes_nothing()
    {
        if (!_fx.Available) return;
        var repo = new CashMallRepository(_fx.Db!);
        var account = NewAccount();
        await SeedBalanceAsync(account, 50);

        var after = await repo.TryPurchaseAsync(account, 51, new[] { Purchase(11) }, new[] { Entry(21) });

        Assert.Null(after);
        Assert.Equal(50L, await repo.GetBalanceAsync(account));
        Assert.Empty(await repo.GetPurchaseHistoryAsync(account));
        Assert.Empty(await repo.GetBoxAsync(account, 1));
    }

    [Fact]
    public async Task TryPurchase_exact_balance_drains_to_zero()
    {
        if (!_fx.Available) return;
        var repo = new CashMallRepository(_fx.Db!);
        var account = NewAccount();
        await SeedBalanceAsync(account, 70);
        Assert.Equal((long?)0, await repo.TryPurchaseAsync(account, 70, new[] { Purchase(11) }, Array.Empty<CashMallBoxEntry>()));
        Assert.Equal(0L, await repo.GetBalanceAsync(account));
        Assert.Single(await repo.GetPurchaseHistoryAsync(account));
    }

    [Fact]
    public async Task TryPurchase_unknown_account_is_null_and_creates_nothing()
    {
        if (!_fx.Available) return;
        var repo = new CashMallRepository(_fx.Db!);
        var account = NewAccount();
        Assert.Null(await repo.TryPurchaseAsync(account, 10, new[] { Purchase(11) }, new[] { Entry(21) }));
        Assert.Equal(0L, await AccountRowsAsync(account));
        Assert.Empty(await repo.GetPurchaseHistoryAsync(account));
    }

    [Fact]
    public async Task TryPurchase_without_log_or_entries_only_debits()
    {
        if (!_fx.Available) return;
        var repo = new CashMallRepository(_fx.Db!);
        var account = NewAccount();
        await SeedBalanceAsync(account, 10);
        Assert.Equal((long?)4, await repo.TryPurchaseAsync(account, 6, Array.Empty<CashMallPurchase>(), Array.Empty<CashMallBoxEntry>()));
        Assert.Empty(await repo.GetPurchaseHistoryAsync(account));
        Assert.Equal((0, 0), await repo.GetBoxCountsAsync(account));
    }

    [Fact]
    public async Task TryPurchase_appends_to_existing_history_oldest_first()
    {
        if (!_fx.Available) return;
        var repo = new CashMallRepository(_fx.Db!);
        var account = NewAccount();
        await SeedBalanceAsync(account, 100);
        await repo.TryPurchaseAsync(account, 10, new[] { Purchase(30) }, Array.Empty<CashMallBoxEntry>());
        await repo.TryPurchaseAsync(account, 10, new[] { Purchase(10) }, Array.Empty<CashMallBoxEntry>());
        await repo.TryPurchaseAsync(account, 10, new[] { Purchase(20) }, Array.Empty<CashMallBoxEntry>());
        
        Assert.Equal(new uint[] { 30, 10, 20 }, (await repo.GetPurchaseHistoryAsync(account)).Select(p => p.OrderId).ToArray());
    }

    [Fact]
    public async Task TryPurchase_concurrent_debits_never_overspend()
    {
        if (!_fx.Available) return;
        var repo = new CashMallRepository(_fx.Db!);
        var account = NewAccount();
        await SeedBalanceAsync(account, 100);

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(i =>
            repo.TryPurchaseAsync(account, 30, new[] { Purchase((uint)(100 + i)) }, Array.Empty<CashMallBoxEntry>())));

        var won = results.Where(r => r.HasValue).Select(r => r!.Value).OrderBy(v => v).ToArray();
        Assert.Equal(new long[] { 10, 40, 70 }, won);
        Assert.Equal(10L, await repo.GetBalanceAsync(account));
        Assert.Equal(3, (await repo.GetPurchaseHistoryAsync(account)).Count);
    }

    [Fact]
    public async Task Purchase_and_entry_fields_round_trip()
    {
        if (!_fx.Available) return;
        var repo = new CashMallRepository(_fx.Db!);
        var account = NewAccount();
        await SeedBalanceAsync(account, 1_000);
        var bought = new DateTime(2026, 10, 17, 8, 30, 15, 250, DateTimeKind.Utc);
        var purchase = new CashMallPurchase
        {
            OrderId = 4_000_000_001, ItemId = 4_000_000_002, ProductId = 4_000_000_003, Quantity = ushort.MaxValue,
            UnitPrice = 5_000_000_000L, BoughtUtc = bought, Gift = true,
            SenderName = "Buyer", RecipientName = "Friend", RecipientAccountId = 4_000_000_004,
        };
        var entry = Entry(4_000_000_005, box: 2, item: 4_000_000_006, count: 7, filed: bought, sender: "Buyer");

        await repo.TryPurchaseAsync(account, 1, new[] { purchase }, new[] { entry });

        var p = Assert.Single(await repo.GetPurchaseHistoryAsync(account));
        Assert.Equal(4_000_000_001u, p.OrderId);
        Assert.Equal(4_000_000_002u, p.ItemId);
        Assert.Equal(4_000_000_003u, p.ProductId);
        Assert.Equal(ushort.MaxValue, p.Quantity);
        Assert.Equal(5_000_000_000L, p.UnitPrice);
        Assert.Equal(bought, p.BoughtUtc);
        Assert.Equal(DateTimeKind.Utc, p.BoughtUtc.Kind);
        Assert.True(p.Gift);
        Assert.Equal("Buyer", p.SenderName);
        Assert.Equal("Friend", p.RecipientName);
        Assert.Equal(4_000_000_004u, p.RecipientAccountId);

        var e = Assert.Single(await repo.GetBoxAsync(account, 2));
        Assert.Equal(4_000_000_005u, e.EntryId);
        Assert.Equal((byte)2, e.Box);
        Assert.Equal(4_000_000_006u, e.ItemId);
        Assert.Equal(7, e.Count);
        Assert.Equal(bought, e.FiledUtc);
        Assert.Equal(DateTimeKind.Utc, e.FiledUtc.Kind);
        Assert.Equal("Buyer", e.SenderName);
    }

    [Fact]
    public async Task Local_and_unspecified_dates_are_stored_as_utc()
    {
        if (!_fx.Available) return;
        var repo = new CashMallRepository(_fx.Db!);
        var account = NewAccount();
        var local = new DateTime(2026, 10, 18, 9, 0, 0, DateTimeKind.Local);
        var unspecified = new DateTime(2026, 10, 18, 10, 0, 0, DateTimeKind.Unspecified);
        await repo.FileGiftAsync(account, new[]
        {
            Entry(1, filed: local), Entry(2, filed: unspecified),
        });
        var byId = (await repo.GetBoxAsync(account, 1)).ToDictionary(e => e.EntryId);
        Assert.Equal(local.ToUniversalTime(), byId[1].FiledUtc);
        Assert.Equal(DateTime.SpecifyKind(unspecified, DateTimeKind.Utc), byId[2].FiledUtc);
    }

    [Fact]
    public async Task Refund_restores_balance_and_drops_the_purchase_and_entries()
    {
        if (!_fx.Available) return;
        var repo = new CashMallRepository(_fx.Db!);
        var account = NewAccount();
        await SeedBalanceAsync(account, 100);
        await repo.TryPurchaseAsync(account, 20, new[] { Purchase(1) }, new[] { Entry(10) });          
        await repo.TryPurchaseAsync(account, 60, new[] { Purchase(2), Purchase(3) }, new[] { Entry(11), Entry(12) });

        await repo.RefundAsync(account, 60, new uint[] { 2, 3 }, new uint[] { 11, 12 });

        Assert.Equal(80L, await repo.GetBalanceAsync(account));
        Assert.Equal(new uint[] { 1 }, (await repo.GetPurchaseHistoryAsync(account)).Select(p => p.OrderId).ToArray());
        Assert.Equal(new uint[] { 10 }, (await repo.GetBoxAsync(account, 1)).Select(e => e.EntryId).ToArray());
    }

    [Fact]
    public async Task Refund_with_empty_id_lists_only_gives_the_balance_back()
    {
        if (!_fx.Available) return;
        var repo = new CashMallRepository(_fx.Db!);
        var account = NewAccount();
        await SeedBalanceAsync(account, 100);
        await repo.TryPurchaseAsync(account, 30, new[] { Purchase(1) }, new[] { Entry(10) });

        await repo.RefundAsync(account, 30, Array.Empty<uint>(), Array.Empty<uint>());

        Assert.Equal(100L, await repo.GetBalanceAsync(account));
        Assert.Single(await repo.GetPurchaseHistoryAsync(account));
        Assert.Single(await repo.GetBoxAsync(account, 1));
    }

    [Fact]
    public async Task Refund_for_unknown_account_is_a_no_op()
    {
        if (!_fx.Available) return;
        var repo = new CashMallRepository(_fx.Db!);
        var account = NewAccount();
        await repo.RefundAsync(account, 500, new uint[] { 1 }, new uint[] { 1 });
        Assert.Equal(0L, await repo.GetBalanceAsync(account));
        Assert.Equal(0L, await AccountRowsAsync(account));
    }

    [Fact]
    public async Task FileGift_creates_the_account_and_files_the_entries_in_their_boxes()
    {
        if (!_fx.Available) return;
        var repo = new CashMallRepository(_fx.Db!);
        var account = NewAccount();

        await repo.FileGiftAsync(account, new[]
        {
            Entry(1, box: 2, count: 3, sender: "Friend"),
            Entry(2, box: 1),
            Entry(3, box: 2),
        });

        Assert.Equal(1L, await AccountRowsAsync(account));
        Assert.Equal(0L, await repo.GetBalanceAsync(account));
        Assert.Equal((1, 2), await repo.GetBoxCountsAsync(account));
        var gift = (await repo.GetBoxAsync(account, 2)).Single(e => e.EntryId == 1);
        Assert.Equal(3, gift.Count);
        Assert.Equal("Friend", gift.SenderName);
    }

    [Fact]
    public async Task FileGift_appends_to_an_existing_account_without_touching_its_balance()
    {
        if (!_fx.Available) return;
        var repo = new CashMallRepository(_fx.Db!);
        var account = NewAccount();
        await SeedBalanceAsync(account, 77);
        await repo.FileGiftAsync(account, new[] { Entry(1, box: 2) });
        await repo.FileGiftAsync(account, new[] { Entry(2, box: 2) });
        Assert.Equal(77L, await repo.GetBalanceAsync(account));
        Assert.Equal((0, 2), await repo.GetBoxCountsAsync(account));
    }

    [Fact]
    public async Task FileGift_with_no_entries_does_nothing()
    {
        if (!_fx.Available) return;
        var repo = new CashMallRepository(_fx.Db!);
        var account = NewAccount();
        await repo.FileGiftAsync(account, Array.Empty<CashMallBoxEntry>());
        Assert.Equal(0L, await AccountRowsAsync(account));
    }

    [Fact]
    public async Task GetBox_filters_by_box_hides_empty_entries_and_orders_newest_first()
    {
        if (!_fx.Available) return;
        var repo = new CashMallRepository(_fx.Db!);
        var account = NewAccount();
        await repo.FileGiftAsync(account, new[]
        {
            Entry(1, box: 1, filed: Day),
            Entry(2, box: 1, filed: Day.AddDays(1)),
            Entry(5, box: 1, filed: Day),                
            Entry(9, box: 1, filed: Day.AddDays(2), count: 0), 
            Entry(7, box: 2, filed: Day.AddDays(3)),
        });

        Assert.Equal(new uint[] { 2, 5, 1 }, (await repo.GetBoxAsync(account, 1)).Select(e => e.EntryId).ToArray());
        Assert.Equal(new uint[] { 7 }, (await repo.GetBoxAsync(account, 2)).Select(e => e.EntryId).ToArray());
        Assert.Empty(await repo.GetBoxAsync(account, 3));
    }

    [Fact]
    public async Task GetBoxEntry_finds_either_box_and_hides_empty_or_unknown()
    {
        if (!_fx.Available) return;
        var repo = new CashMallRepository(_fx.Db!);
        var account = NewAccount();
        await repo.FileGiftAsync(account, new[]
        {
            Entry(1, box: 1, count: 4, sender: "A"),
            Entry(2, box: 2, count: 5, sender: "B"),
            Entry(3, box: 1, count: 0),
        });

        var one = await repo.GetBoxEntryAsync(account, 1);
        Assert.NotNull(one);
        Assert.Equal(4, one!.Count);
        Assert.Equal("A", one.SenderName);
        var two = await repo.GetBoxEntryAsync(account, 2);
        Assert.NotNull(two);
        Assert.Equal((byte)2, two!.Box);
        Assert.Null(await repo.GetBoxEntryAsync(account, 3));   
        Assert.Null(await repo.GetBoxEntryAsync(account, 99));  
        Assert.Null(await repo.GetBoxEntryAsync(NewAccount(), 1)); 
    }

    [Fact]
    public async Task GetBoxCounts_counts_live_entries_not_units()
    {
        if (!_fx.Available) return;
        var repo = new CashMallRepository(_fx.Db!);
        var account = NewAccount();
        await repo.FileGiftAsync(account, new[]
        {
            Entry(1, box: 1, count: 10),
            Entry(2, box: 1, count: 0),
            Entry(3, box: 2, count: 1),
            Entry(4, box: 2, count: 2),
            Entry(5, box: 2, count: 0),
        });
        Assert.Equal((1, 2), await repo.GetBoxCountsAsync(account));
    }

    [Fact]
    public async Task TryTakeUnits_partial_take_returns_the_entry_before_and_leaves_the_rest()
    {
        if (!_fx.Available) return;
        var repo = new CashMallRepository(_fx.Db!);
        var account = NewAccount();
        await repo.FileGiftAsync(account, new[] { Entry(1, box: 2, item: 555, count: 5, sender: "Friend") });

        var before = await repo.TryTakeUnitsAsync(account, 1, 2);

        Assert.NotNull(before);
        Assert.Equal(1u, before!.EntryId);
        Assert.Equal((byte)2, before.Box);
        Assert.Equal(555u, before.ItemId);
        Assert.Equal(5, before.Count);          
        Assert.Equal("Friend", before.SenderName);
        Assert.Equal(Day, before.FiledUtc);
        Assert.Equal(3, (await repo.GetBoxEntryAsync(account, 1))!.Count);
    }

    [Fact]
    public async Task TryTakeUnits_taking_everything_removes_the_entry()
    {
        if (!_fx.Available) return;
        var repo = new CashMallRepository(_fx.Db!);
        var account = NewAccount();
        await repo.FileGiftAsync(account, new[] { Entry(1, count: 3), Entry(2, count: 1) });

        var before = await repo.TryTakeUnitsAsync(account, 1, 3);

        Assert.Equal(3, before!.Count);
        Assert.Null(await repo.GetBoxEntryAsync(account, 1));
        Assert.Equal(new uint[] { 2 }, (await repo.GetBoxAsync(account, 1)).Select(e => e.EntryId).ToArray());
        Assert.Equal((1, 0), await repo.GetBoxCountsAsync(account));
        
        Assert.Null(await repo.TryTakeUnitsAsync(account, 1, 1));
    }

    [Fact]
    public async Task TryTakeUnits_more_than_held_is_null_and_changes_nothing()
    {
        if (!_fx.Available) return;
        var repo = new CashMallRepository(_fx.Db!);
        var account = NewAccount();
        await repo.FileGiftAsync(account, new[] { Entry(1, count: 2) });
        Assert.Null(await repo.TryTakeUnitsAsync(account, 1, 3));
        Assert.Equal(2, (await repo.GetBoxEntryAsync(account, 1))!.Count);
    }

    [Fact]
    public async Task TryTakeUnits_non_positive_units_unknown_entry_and_unknown_account_are_null()
    {
        if (!_fx.Available) return;
        var repo = new CashMallRepository(_fx.Db!);
        var account = NewAccount();
        await repo.FileGiftAsync(account, new[] { Entry(1, count: 2) });
        Assert.Null(await repo.TryTakeUnitsAsync(account, 1, 0));
        Assert.Null(await repo.TryTakeUnitsAsync(account, 1, -1));
        Assert.Null(await repo.TryTakeUnitsAsync(account, 42, 1));
        Assert.Null(await repo.TryTakeUnitsAsync(NewAccount(), 1, 1));
        Assert.Equal(2, (await repo.GetBoxEntryAsync(account, 1))!.Count);
    }

    [Fact]
    public async Task TryTakeUnits_does_not_touch_other_entries_or_the_balance()
    {
        if (!_fx.Available) return;
        var repo = new CashMallRepository(_fx.Db!);
        var account = NewAccount();
        await SeedBalanceAsync(account, 9);
        await repo.FileGiftAsync(account, new[] { Entry(1, count: 4), Entry(2, count: 4) });
        await repo.TryTakeUnitsAsync(account, 1, 4);
        Assert.Equal(4, (await repo.GetBoxEntryAsync(account, 2))!.Count);
        Assert.Equal(9L, await repo.GetBalanceAsync(account));
    }

    [Fact]
    public async Task TryTakeUnits_concurrent_takes_never_hand_out_more_than_held()
    {
        if (!_fx.Available) return;
        var repo = new CashMallRepository(_fx.Db!);
        var account = NewAccount();
        await repo.FileGiftAsync(account, new[] { Entry(1, count: 5) });

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => repo.TryTakeUnitsAsync(account, 1, 1)));

        Assert.Equal(5, results.Count(r => r is not null));
        Assert.Null(await repo.GetBoxEntryAsync(account, 1));
    }

    [Fact]
    public async Task ReturnUnits_adds_to_the_entry_when_it_is_still_there()
    {
        if (!_fx.Available) return;
        var repo = new CashMallRepository(_fx.Db!);
        var account = NewAccount();
        await repo.FileGiftAsync(account, new[] { Entry(1, box: 2, count: 5, sender: "Orig") });
        var taken = await repo.TryTakeUnitsAsync(account, 1, 2);

        await repo.ReturnUnitsAsync(account, taken!, 2);

        var back = (await repo.GetBoxEntryAsync(account, 1))!;
        Assert.Equal(5, back.Count);
        Assert.Equal("Orig", back.SenderName);
        Assert.Equal((0, 1), await repo.GetBoxCountsAsync(account));
    }

    [Fact]
    public async Task ReturnUnits_refiles_the_entry_when_it_was_emptied_and_removed()
    {
        if (!_fx.Available) return;
        var repo = new CashMallRepository(_fx.Db!);
        var account = NewAccount();
        var filed = Day.AddHours(5);
        await repo.FileGiftAsync(account, new[] { Entry(1, box: 2, item: 555, count: 3, filed: filed, sender: "Friend") });
        var taken = await repo.TryTakeUnitsAsync(account, 1, 3);
        Assert.Null(await repo.GetBoxEntryAsync(account, 1));

        
        await repo.ReturnUnitsAsync(account, taken!, 2);

        var back = (await repo.GetBoxEntryAsync(account, 1))!;
        Assert.Equal(2, back.Count);
        Assert.Equal((byte)2, back.Box);
        Assert.Equal(555u, back.ItemId);
        Assert.Equal(filed, back.FiledUtc);
        Assert.Equal("Friend", back.SenderName);
    }

    [Fact]
    public async Task ReturnUnits_for_unknown_account_creates_it_and_files_the_entry()
    {
        if (!_fx.Available) return;
        var repo = new CashMallRepository(_fx.Db!);
        var account = NewAccount();

        await repo.ReturnUnitsAsync(account, Entry(8, box: 1, item: 77, count: 99, sender: "S"), 4);

        Assert.Equal(1L, await AccountRowsAsync(account));
        var back = (await repo.GetBoxEntryAsync(account, 8))!;
        Assert.Equal(4, back.Count);      
        Assert.Equal(77u, back.ItemId);
    }

    [Fact]
    public async Task ReturnUnits_non_positive_units_is_a_no_op()
    {
        if (!_fx.Available) return;
        var repo = new CashMallRepository(_fx.Db!);
        var account = NewAccount();
        await repo.FileGiftAsync(account, new[] { Entry(1, count: 2) });

        await repo.ReturnUnitsAsync(account, Entry(1), 0);
        await repo.ReturnUnitsAsync(account, Entry(1), -3);
        await repo.ReturnUnitsAsync(account, Entry(50), 0);

        Assert.Equal(2, (await repo.GetBoxEntryAsync(account, 1))!.Count);
        Assert.Null(await repo.GetBoxEntryAsync(account, 50));
    }
}
