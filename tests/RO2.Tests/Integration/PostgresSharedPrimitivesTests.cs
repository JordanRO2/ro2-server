using Npgsql;
using RO2.Domain.Entities;
using RO2.Persistence.Postgres.Repositories;
using Xunit;

namespace RO2.Tests.Integration;

/// <summary>
/// Postgres proof of the SHARED-A economy primitives: <see cref="InventoryRepository.TryReassignAsync"/> (owner-
/// filtered, single-statement re-home) and <see cref="MailRepository.TryClaimItemAsync"/> /
/// <see cref="MailRepository.TryClaimGoldAsync"/> (guarded flag flip, exactly one winner). Soft-skips without Docker.
/// </summary>
[Collection("Postgres")]
public sealed class PostgresSharedPrimitivesTests
{
    private readonly PostgresFixture _fx;
    public PostgresSharedPrimitivesTests(PostgresFixture fx) => _fx = fx;

    private static long _keys = 3_500_000_000L + Random.Shared.Next(0, 100_000_000);
    private static uint NewKey() => (uint)Interlocked.Increment(ref _keys);

    private static InventoryItem NewItem(uint charId, short slot, byte storage = 0, int count = 1) =>
        new() { CharacterId = charId, ItemId = 5000, Count = count, Slot = slot, StorageType = storage };

    // ---- TryReassign ----

    [Fact]
    public async Task TryReassign_moves_the_owned_row_and_sets_storage_and_slot()
    {
        if (!_fx.Available) return;
        var repo = new InventoryRepository(_fx.Db!);
        var from = NewKey();
        var to = NewKey();
        var item = NewItem(from, slot: 4, count: 7);
        await repo.AddItemAsync(item);

        Assert.True(await repo.TryReassignAsync(item.Id, from, to, toStorageType: 2, toSlot: 9));

        var got = (await repo.GetByIdAsync(item.Id))!;
        Assert.Equal(to, got.CharacterId);
        Assert.Equal((byte)2, got.StorageType);
        Assert.Equal((short)9, got.Slot);
        Assert.Equal(7, got.Count); // untouched
    }

    [Fact]
    public async Task TryReassign_filters_on_the_owner_and_changes_nothing_otherwise()
    {
        if (!_fx.Available) return;
        var repo = new InventoryRepository(_fx.Db!);
        var owner = NewKey();
        var item = NewItem(owner, slot: 1);
        await repo.AddItemAsync(item);

        Assert.False(await repo.TryReassignAsync(item.Id, NewKey(), NewKey(), 0, 3)); // forged "from"
        Assert.False(await repo.TryReassignAsync(item.Id + 1_000_000_000UL, owner, NewKey(), 0, 3)); // missing row

        var got = (await repo.GetByIdAsync(item.Id))!;
        Assert.Equal(owner, got.CharacterId);
        Assert.Equal((short)1, got.Slot);
    }

    [Fact]
    public async Task TryReassign_second_attempt_from_the_old_owner_fails()
    {
        if (!_fx.Available) return;
        var repo = new InventoryRepository(_fx.Db!);
        var from = NewKey();
        var item = NewItem(from, slot: 0);
        await repo.AddItemAsync(item);

        Assert.True(await repo.TryReassignAsync(item.Id, from, NewKey(), 0, 0));
        Assert.False(await repo.TryReassignAsync(item.Id, from, NewKey(), 0, 0)); // no longer the sender's
    }

    [Fact]
    public async Task TryReassign_into_an_occupied_slot_throws_and_leaves_the_row_in_place()
    {
        if (!_fx.Available) return;
        var repo = new InventoryRepository(_fx.Db!);
        var from = NewKey();
        var to = NewKey();
        var moving = NewItem(from, slot: 2);
        var blocker = NewItem(to, slot: 5);
        await repo.AddItemAsync(moving);
        await repo.AddItemAsync(blocker);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => repo.TryReassignAsync(moving.Id, from, to, 0, 5));
        Assert.Equal("23505", ex.SqlState);

        var got = (await repo.GetByIdAsync(moving.Id))!;
        Assert.Equal(from, got.CharacterId);
        Assert.Equal((short)2, got.Slot);
    }

    [Fact]
    public async Task TryReassign_concurrent_distinct_slots_all_land_and_the_same_row_has_one_winner()
    {
        if (!_fx.Available) return;
        var repo = new InventoryRepository(_fx.Db!);
        var from = NewKey();
        var to = NewKey();

        // Eight different rows moved at once into eight distinct destination slots: every one lands.
        var items = Enumerable.Range(0, 8).Select(n => NewItem(from, slot: (short)n)).ToList();
        foreach (var i in items) await repo.AddItemAsync(i);
        var moved = await Task.WhenAll(items.Select((i, n) => repo.TryReassignAsync(i.Id, from, to, 0, (short)(10 + n))));
        Assert.All(moved, Assert.True);
        var dest = await repo.GetByCharacterAsync(to);
        Assert.Equal(8, dest.Count);
        Assert.Equal(8, dest.Select(r => r.Slot).Distinct().Count());
        Assert.Empty(await repo.GetByCharacterAsync(from));

        // One row raced by eight "senders": exactly one move wins, the rest see it gone.
        var contested = NewItem(from, slot: 0);
        await repo.AddItemAsync(contested);
        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(n => repo.TryReassignAsync(contested.Id, from, NewKey(), 0, (short)n)));
        Assert.Equal(1, results.Count(r => r));
    }

    // ---- TryClaim ----

    private static MailMessage Draft(uint receiver, Action<MailMessage>? shape = null)
    {
        var m = new MailMessage
        {
            ReceiverCharId = receiver, ReceiverName = "Rcv", SenderCharId = 7, SenderName = "Snd",
            Title = "T", Body = "B", SentAt = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc),
        };
        shape?.Invoke(m);
        return m;
    }

    [Fact]
    public async Task TryClaimItem_returns_the_mail_once_and_flips_the_flag()
    {
        if (!_fx.Available) return;
        var repo = new MailRepository(_fx.Db!);
        var rcv = NewKey();
        var added = await repo.AddAsync(Draft(rcv, m => { m.AttachedItemId = 501; m.AttachedItemCount = 3; }));

        var first = await repo.TryClaimItemAsync(rcv, added.Uid);
        Assert.NotNull(first);
        Assert.Equal(501u, first!.AttachedItemId);
        Assert.Equal(3, first.AttachedItemCount);
        Assert.True(first.ItemClaimed);
        Assert.Null(await repo.TryClaimItemAsync(rcv, added.Uid));
        Assert.True((await repo.GetByUidAsync(rcv, added.Uid))!.ItemClaimed);
    }

    [Fact]
    public async Task TryClaimItem_double_claim_race_exactly_one_non_null()
    {
        if (!_fx.Available) return;
        var repo = new MailRepository(_fx.Db!);
        var rcv = NewKey();
        var added = await repo.AddAsync(Draft(rcv, m => m.AttachedItemId = 501));

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => repo.TryClaimItemAsync(rcv, added.Uid)));

        Assert.Equal(1, results.Count(r => r is not null));
    }

    [Fact]
    public async Task TryClaimGold_double_claim_race_exactly_one_non_null()
    {
        if (!_fx.Available) return;
        var repo = new MailRepository(_fx.Db!);
        var rcv = NewKey();
        var added = await repo.AddAsync(Draft(rcv, m => m.Gold = 12_345));

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => repo.TryClaimGoldAsync(rcv, added.Uid)));

        var winner = Assert.Single(results, r => r is not null);
        Assert.Equal(12_345L, winner!.Gold);
        Assert.True(winner.GoldClaimed);
        Assert.True((await repo.GetByUidAsync(rcv, added.Uid))!.GoldClaimed);
    }

    [Fact]
    public async Task TryClaim_refuses_wrong_receiver_nothing_attached_and_already_claimed()
    {
        if (!_fx.Available) return;
        var repo = new MailRepository(_fx.Db!);
        var rcv = NewKey();
        var withBoth = await repo.AddAsync(Draft(rcv, m => { m.AttachedItemId = 501; m.Gold = 100; }));
        var empty = await repo.AddAsync(Draft(rcv));
        var spent = await repo.AddAsync(Draft(rcv, m =>
            { m.AttachedItemId = 501; m.ItemClaimed = true; m.Gold = 100; m.GoldClaimed = true; }));

        Assert.Null(await repo.TryClaimItemAsync(NewKey(), withBoth.Uid)); // not the receiver's
        Assert.Null(await repo.TryClaimGoldAsync(NewKey(), withBoth.Uid));
        Assert.Null(await repo.TryClaimItemAsync(rcv, empty.Uid));          // no item
        Assert.Null(await repo.TryClaimGoldAsync(rcv, empty.Uid));          // no zeny
        Assert.Null(await repo.TryClaimItemAsync(rcv, spent.Uid));          // already claimed
        Assert.Null(await repo.TryClaimGoldAsync(rcv, spent.Uid));

        var intact = (await repo.GetByUidAsync(rcv, withBoth.Uid))!;
        Assert.False(intact.ItemClaimed);
        Assert.False(intact.GoldClaimed);
    }

    [Fact]
    public async Task TryClaim_item_and_gold_flip_independently()
    {
        if (!_fx.Available) return;
        var repo = new MailRepository(_fx.Db!);
        var rcv = NewKey();
        var added = await repo.AddAsync(Draft(rcv, m => { m.AttachedItemId = 501; m.Gold = 100; }));

        Assert.NotNull(await repo.TryClaimItemAsync(rcv, added.Uid));
        var afterItem = (await repo.GetByUidAsync(rcv, added.Uid))!;
        Assert.True(afterItem.ItemClaimed);
        Assert.False(afterItem.GoldClaimed);

        Assert.NotNull(await repo.TryClaimGoldAsync(rcv, added.Uid));
        Assert.True(await repo.DeleteIfSpentAsync(rcv, added.Uid)); // both claimed: now spent
    }
}
