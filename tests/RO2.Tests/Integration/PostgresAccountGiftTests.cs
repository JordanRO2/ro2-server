using RO2.Domain.Entities;
using RO2.Persistence.Postgres.Repositories;
using Xunit;

namespace RO2.Tests.Integration;

/// <summary>
/// Runtime tests for <see cref="AccountGiftRepository"/> against a live PostgreSQL instance
/// (<see cref="PostgresFixture"/>). Covers filing, live and expired listing with purge, claiming, attendance
/// reads, and the attendance compare-and-set (first day, next day, stale reader, concurrent callers, and a
/// reward filed in the same write).
/// </summary>
/// <remarks>Each test uses a fresh random account id so tests can share one container.</remarks>
[Collection("Postgres")]
public sealed class PostgresAccountGiftTests
{
    
    private static readonly DateTime NowLocal = new(2026, 6, 15, 12, 0, 0, DateTimeKind.Local);

    private readonly PostgresFixture _fx;
    public PostgresAccountGiftTests(PostgresFixture fx) => _fx = fx;

    
    private static uint NewAccount() => (uint)Random.Shared.NextInt64(3_000_000_000, 4_000_000_000);

    private static AccountGift Gift(int daysAgo, uint itemId = 501, ushort count = 3, byte source = 1) => new()
    {
        Guid = System.Guid.NewGuid().ToString("N"),
        ItemId = itemId,
        Count = count,
        ReceivedUtc = NowLocal.AddDays(-daysAgo).ToUniversalTime(),
        Source = source,
    };

    [Fact]
    public async Task GetLive_unknown_account_is_empty()
    {
        if (!_fx.Available) return;
        var repo = new AccountGiftRepository(_fx.Db!);
        Assert.Empty(await repo.GetLiveAsync(NewAccount(), NowLocal));
    }

    [Fact]
    public async Task File_creates_box_returns_live_count_and_round_trips_fields()
    {
        if (!_fx.Available) return;
        var repo = new AccountGiftRepository(_fx.Db!);
        var account = NewAccount();
        var gift = Gift(0, itemId: 4_000_000_001, count: ushort.MaxValue, source: 4);

        Assert.Equal(1, await repo.FileAsync(account, new[] { gift }, NowLocal));

        var live = Assert.Single(await repo.GetLiveAsync(account, NowLocal));
        Assert.Equal(gift.Guid, live.Guid);
        Assert.Equal(4_000_000_001u, live.ItemId);
        Assert.Equal(ushort.MaxValue, live.Count);
        Assert.Equal((byte)4, live.Source);
        Assert.Equal(DateTimeKind.Utc, live.ReceivedUtc.Kind);
        Assert.True((gift.ReceivedUtc - live.ReceivedUtc).Duration() < TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task File_appends_to_existing_box_and_counts_all_live()
    {
        if (!_fx.Available) return;
        var repo = new AccountGiftRepository(_fx.Db!);
        var account = NewAccount();

        Assert.Equal(2, await repo.FileAsync(account, new[] { Gift(0), Gift(1) }, NowLocal));
        Assert.Equal(3, await repo.FileAsync(account, new[] { Gift(2) }, NowLocal));
        Assert.Equal(3, (await repo.GetLiveAsync(account, NowLocal)).Count);
    }

    [Fact]
    public async Task File_empty_list_creates_nothing_and_counts_zero()
    {
        if (!_fx.Available) return;
        var repo = new AccountGiftRepository(_fx.Db!);
        var account = NewAccount();

        Assert.Equal(0, await repo.FileAsync(account, Array.Empty<AccountGift>(), NowLocal));
        Assert.Empty(await repo.GetLiveAsync(account, NowLocal));
        
        Assert.False(await repo.TryTakeAsync(account, "anything"));
    }

    [Fact]
    public async Task File_does_not_count_expired_gifts()
    {
        if (!_fx.Available) return;
        var repo = new AccountGiftRepository(_fx.Db!);
        var account = NewAccount();

        
        Assert.Equal(1, await repo.FileAsync(account, new[] { Gift(7), Gift(8) }, NowLocal));
    }

    [Fact]
    public async Task GetLive_is_newest_first_and_purges_expired_from_storage()
    {
        if (!_fx.Available) return;
        var repo = new AccountGiftRepository(_fx.Db!);
        var account = NewAccount();
        var old = Gift(5);
        var recent = Gift(1);
        var expired = Gift(9);
        var edge = Gift(7);
        await repo.FileAsync(account, new[] { old, recent, expired, edge }, NowLocal);

        var live = await repo.GetLiveAsync(account, NowLocal);
        Assert.Equal(new[] { recent.Guid, old.Guid, edge.Guid }, live.Select(g => g.Guid).ToArray());

        
        var earlier = await repo.GetLiveAsync(account, NowLocal.AddDays(-5));
        Assert.DoesNotContain(earlier, g => g.Guid == expired.Guid);
        Assert.Equal(3, earlier.Count);
    }

    [Fact]
    public async Task GetLive_purge_leaves_other_accounts_untouched()
    {
        if (!_fx.Available) return;
        var repo = new AccountGiftRepository(_fx.Db!);
        var a = NewAccount();
        var b = NewAccount();
        var expiredB = Gift(9);
        await repo.FileAsync(a, new[] { Gift(9), Gift(0) }, NowLocal.AddDays(-10)); 
        await repo.FileAsync(b, new[] { expiredB }, NowLocal.AddDays(-10));

        Assert.Single(await repo.GetLiveAsync(a, NowLocal)); 
        
        var bAtEarlier = Assert.Single(await repo.GetLiveAsync(b, NowLocal.AddDays(-10)));
        Assert.Equal(expiredB.Guid, bAtEarlier.Guid);
    }

    [Fact]
    public async Task TryTake_removes_the_gift_once()
    {
        if (!_fx.Available) return;
        var repo = new AccountGiftRepository(_fx.Db!);
        var account = NewAccount();
        var keep = Gift(0);
        var take = Gift(1);
        await repo.FileAsync(account, new[] { keep, take }, NowLocal);

        Assert.True(await repo.TryTakeAsync(account, take.Guid));

        var live = Assert.Single(await repo.GetLiveAsync(account, NowLocal));
        Assert.Equal(keep.Guid, live.Guid);
    }

    [Fact]
    public async Task TryTake_unknown_account_is_false()
    {
        if (!_fx.Available) return;
        var repo = new AccountGiftRepository(_fx.Db!);
        Assert.False(await repo.TryTakeAsync(NewAccount(), System.Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public async Task TryTake_missing_guid_on_existing_box_is_true_and_removes_nothing()
    {
        if (!_fx.Available) return;
        var repo = new AccountGiftRepository(_fx.Db!);
        var account = NewAccount();
        var gift = Gift(0);
        await repo.FileAsync(account, new[] { gift }, NowLocal);

        
        Assert.True(await repo.TryTakeAsync(account, System.Guid.NewGuid().ToString("N")));
        Assert.Single(await repo.GetLiveAsync(account, NowLocal));
    }

    [Fact]
    public async Task TryTake_only_pulls_from_the_callers_account()
    {
        if (!_fx.Available) return;
        var repo = new AccountGiftRepository(_fx.Db!);
        var a = NewAccount();
        var b = NewAccount();
        var gift = Gift(0);
        await repo.FileAsync(a, new[] { gift }, NowLocal);
        await repo.FileAsync(b, new[] { Gift(0) }, NowLocal);

        await repo.TryTakeAsync(b, gift.Guid); 

        Assert.Single(await repo.GetLiveAsync(a, NowLocal));
    }

    [Fact]
    public async Task GetAttendance_unknown_account_is_zeroes()
    {
        if (!_fx.Available) return;
        var repo = new AccountGiftRepository(_fx.Db!);
        Assert.Equal(new AccountAttendance(0, 0, 0), await repo.GetAttendanceAsync(NewAccount()));
    }

    [Fact]
    public async Task GetAttendance_box_filed_before_attendance_reads_as_never_attended()
    {
        if (!_fx.Available) return;
        var repo = new AccountGiftRepository(_fx.Db!);
        var account = NewAccount();
        await repo.FileAsync(account, new[] { Gift(0) }, NowLocal);

        Assert.Equal(new AccountAttendance(0, 0, 0), await repo.GetAttendanceAsync(account));
    }

    [Fact]
    public async Task RecordAttendance_first_day_creates_the_box_and_files_the_reward()
    {
        if (!_fx.Available) return;
        var repo = new AccountGiftRepository(_fx.Db!);
        var account = NewAccount();
        var reward = Gift(0, itemId: 607, count: 5, source: 4);

        Assert.True(await repo.TryRecordAttendanceAsync(account, 0, 740_000, 1, 0, reward));

        Assert.Equal(new AccountAttendance(1, 740_000, 0), await repo.GetAttendanceAsync(account));
        var live = Assert.Single(await repo.GetLiveAsync(account, NowLocal));
        Assert.Equal(reward.Guid, live.Guid);
        Assert.Equal(607u, live.ItemId);
        Assert.Equal((ushort)5, live.Count);
        Assert.Equal((byte)4, live.Source);
    }

    [Fact]
    public async Task RecordAttendance_first_day_without_reward_files_nothing()
    {
        if (!_fx.Available) return;
        var repo = new AccountGiftRepository(_fx.Db!);
        var account = NewAccount();

        Assert.True(await repo.TryRecordAttendanceAsync(account, 0, 740_000, 1, 0, null));

        Assert.Equal(new AccountAttendance(1, 740_000, 0), await repo.GetAttendanceAsync(account));
        Assert.Empty(await repo.GetLiveAsync(account, NowLocal));
    }

    [Fact]
    public async Task RecordAttendance_first_day_on_existing_never_attended_box_keeps_its_gifts()
    {
        if (!_fx.Available) return;
        var repo = new AccountGiftRepository(_fx.Db!);
        var account = NewAccount();
        var existing = Gift(2);
        await repo.FileAsync(account, new[] { existing }, NowLocal);
        var reward = Gift(0, source: 4);

        Assert.True(await repo.TryRecordAttendanceAsync(account, 0, 740_000, 1, 0, reward));

        var live = await repo.GetLiveAsync(account, NowLocal);
        Assert.Equal(new[] { reward.Guid, existing.Guid }, live.Select(g => g.Guid).ToArray());
        Assert.Equal(new AccountAttendance(1, 740_000, 0), await repo.GetAttendanceAsync(account));
    }

    [Fact]
    public async Task RecordAttendance_next_day_matches_only_the_day_the_caller_saw()
    {
        if (!_fx.Available) return;
        var repo = new AccountGiftRepository(_fx.Db!);
        var account = NewAccount();
        await repo.TryRecordAttendanceAsync(account, 0, 740_000, 1, 0, null);

        
        Assert.False(await repo.TryRecordAttendanceAsync(account, 739_999, 740_001, 2, 0, Gift(0, source: 4)));
        Assert.Equal(new AccountAttendance(1, 740_000, 0), await repo.GetAttendanceAsync(account));
        Assert.Empty(await repo.GetLiveAsync(account, NowLocal));

        
        var reward = Gift(0, source: 4);
        Assert.True(await repo.TryRecordAttendanceAsync(account, 740_000, 740_003, 2, 1, reward));
        Assert.Equal(new AccountAttendance(2, 740_003, 1), await repo.GetAttendanceAsync(account));
        Assert.Equal(reward.Guid, Assert.Single(await repo.GetLiveAsync(account, NowLocal)).Guid);

        
        Assert.False(await repo.TryRecordAttendanceAsync(account, 740_000, 740_003, 2, 1, Gift(0, source: 4)));
        Assert.Single(await repo.GetLiveAsync(account, NowLocal));
    }

    [Fact]
    public async Task RecordAttendance_seen_zero_after_attending_is_false()
    {
        if (!_fx.Available) return;
        var repo = new AccountGiftRepository(_fx.Db!);
        var account = NewAccount();
        await repo.TryRecordAttendanceAsync(account, 0, 740_000, 1, 0, null);

        
        Assert.False(await repo.TryRecordAttendanceAsync(account, 0, 740_001, 2, 0, Gift(0, source: 4)));
        Assert.Equal(new AccountAttendance(1, 740_000, 0), await repo.GetAttendanceAsync(account));
        Assert.Empty(await repo.GetLiveAsync(account, NowLocal));
    }

    [Fact]
    public async Task RecordAttendance_seen_nonzero_without_a_box_is_false_and_creates_nothing()
    {
        if (!_fx.Available) return;
        var repo = new AccountGiftRepository(_fx.Db!);
        var account = NewAccount();

        Assert.False(await repo.TryRecordAttendanceAsync(account, 740_000, 740_001, 2, 0, Gift(0, source: 4)));
        Assert.Equal(new AccountAttendance(0, 0, 0), await repo.GetAttendanceAsync(account));
        Assert.Empty(await repo.GetLiveAsync(account, NowLocal));
    }

    [Fact]
    public async Task RecordAttendance_concurrent_first_day_has_exactly_one_winner_and_one_reward()
    {
        if (!_fx.Available) return;
        var repo = new AccountGiftRepository(_fx.Db!);
        var account = NewAccount();

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            Task.Run(() => repo.TryRecordAttendanceAsync(account, 0, 740_000, 1, 0, Gift(0, source: 4)))));

        Assert.Equal(1, results.Count(r => r));
        Assert.Equal(new AccountAttendance(1, 740_000, 0), await repo.GetAttendanceAsync(account));
        Assert.Single(await repo.GetLiveAsync(account, NowLocal)); 
    }

    [Fact]
    public async Task RecordAttendance_concurrent_next_day_has_exactly_one_winner_and_one_reward()
    {
        if (!_fx.Available) return;
        var repo = new AccountGiftRepository(_fx.Db!);
        var account = NewAccount();
        await repo.TryRecordAttendanceAsync(account, 0, 740_000, 1, 0, null);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            Task.Run(() => repo.TryRecordAttendanceAsync(account, 740_000, 740_001, 2, 0, Gift(0, source: 4)))));

        Assert.Equal(1, results.Count(r => r));
        Assert.Equal(new AccountAttendance(2, 740_001, 0), await repo.GetAttendanceAsync(account));
        Assert.Single(await repo.GetLiveAsync(account, NowLocal));
    }

    [Fact]
    public async Task Filing_after_attendance_keeps_the_attendance()
    {
        if (!_fx.Available) return;
        var repo = new AccountGiftRepository(_fx.Db!);
        var account = NewAccount();
        await repo.TryRecordAttendanceAsync(account, 0, 740_000, 4, 2, null);

        Assert.Equal(1, await repo.FileAsync(account, new[] { Gift(0) }, NowLocal));
        Assert.Equal(new AccountAttendance(4, 740_000, 2), await repo.GetAttendanceAsync(account));
    }
}
