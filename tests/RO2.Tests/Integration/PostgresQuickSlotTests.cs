using RO2.Domain.Entities;
using RO2.Persistence.Postgres.Repositories;
using Xunit;

namespace RO2.Tests.Integration;

/// <summary>
/// Integration tests for <see cref="QuickSlotRepository"/> against a live PostgreSQL instance.
/// </summary>
/// <remarks>
/// Covers ordered reads, upsert-replace of an occupied position, clear and swap semantics. Each test uses a fresh random identifier so tests can share one container.
/// </remarks>
[Collection("Postgres")]
public sealed class PostgresQuickSlotTests
{
    private readonly PostgresFixture _fx;
    public PostgresQuickSlotTests(PostgresFixture fx) => _fx = fx;

    
    private static uint NewChar() => (uint)Random.Shared.NextInt64(2_200_000_000L, 4_290_000_000L);

    private static CharacterQuickSlot Entry(uint ch, byte page, byte slot, uint entryId,
        byte type = 2, ushort amount = 1, ulong uid = 0) => new()
    {
        CharacterId = ch, Page = page, Slot = slot, EntryType = type,
        EntryId = entryId, Amount = amount, ItemUid = uid,
    };

    [Fact]
    public async Task Get_unknown_character_is_empty()
    {
        if (!_fx.Available) return;
        var repo = new QuickSlotRepository(_fx.Db!);
        Assert.Empty(await repo.GetByCharacterAsync(NewChar()));
    }

    [Fact]
    public async Task Set_roundtrips_every_field_including_large_values()
    {
        if (!_fx.Available) return;
        var repo = new QuickSlotRepository(_fx.Db!);
        var ch = NewChar();
        
        await repo.SetAsync(Entry(ch, 2, 9, 4_000_000_000u, type: 3, amount: 65535, uid: 18_000_000_000_000_000_000UL));

        var got = Assert.Single(await repo.GetByCharacterAsync(ch));
        Assert.Equal(ch, got.CharacterId);
        Assert.Equal((byte)2, got.Page);
        Assert.Equal((byte)9, got.Slot);
        Assert.Equal((byte)3, got.EntryType);
        Assert.Equal(4_000_000_000u, got.EntryId);
        Assert.Equal((ushort)65535, got.Amount);
        Assert.Equal(18_000_000_000_000_000_000UL, got.ItemUid);
        Assert.NotEqual(0UL, got.Id);
    }

    [Fact]
    public async Task Get_orders_by_page_then_slot_and_isolates_characters()
    {
        if (!_fx.Available) return;
        var repo = new QuickSlotRepository(_fx.Db!);
        var ch = NewChar();
        var other = NewChar();
        await repo.SetAsync(Entry(ch, 1, 0, 11));
        await repo.SetAsync(Entry(ch, 0, 5, 12));
        await repo.SetAsync(Entry(ch, 0, 2, 13));
        await repo.SetAsync(Entry(ch, 2, 1, 14));
        await repo.SetAsync(Entry(other, 0, 0, 99));

        var got = await repo.GetByCharacterAsync(ch);
        Assert.Equal(new[] { 13u, 12u, 11u, 14u }, got.Select(s => s.EntryId).ToArray());
        Assert.Equal(new[] { ((byte)0, (byte)2), ((byte)0, (byte)5), ((byte)1, (byte)0), ((byte)2, (byte)1) },
            got.Select(s => (s.Page, s.Slot)).ToArray());
        Assert.Equal(99u, Assert.Single(await repo.GetByCharacterAsync(other)).EntryId);
    }

    [Fact]
    public async Task Set_on_occupied_position_replaces_and_keeps_row_id()
    {
        if (!_fx.Available) return;
        var repo = new QuickSlotRepository(_fx.Db!);
        var ch = NewChar();
        await repo.SetAsync(Entry(ch, 0, 3, 100, type: 1, amount: 5, uid: 777));
        var first = Assert.Single(await repo.GetByCharacterAsync(ch));

        await repo.SetAsync(Entry(ch, 0, 3, 200, type: 2, amount: 9, uid: 0));

        var second = Assert.Single(await repo.GetByCharacterAsync(ch));
        Assert.Equal(200u, second.EntryId);
        Assert.Equal((byte)2, second.EntryType);
        Assert.Equal((ushort)9, second.Amount);
        Assert.Equal(0UL, second.ItemUid);
        Assert.Equal(first.Id, second.Id); 
    }

    [Fact]
    public async Task Clear_removes_only_that_position()
    {
        if (!_fx.Available) return;
        var repo = new QuickSlotRepository(_fx.Db!);
        var ch = NewChar();
        var other = NewChar();
        await repo.SetAsync(Entry(ch, 0, 0, 1));
        await repo.SetAsync(Entry(ch, 0, 1, 2));
        await repo.SetAsync(Entry(ch, 1, 0, 3));
        await repo.SetAsync(Entry(other, 0, 0, 4));

        await repo.ClearAsync(ch, 0, 0);

        Assert.Equal(new[] { 2u, 3u }, (await repo.GetByCharacterAsync(ch)).Select(s => s.EntryId).ToArray());
        Assert.Single(await repo.GetByCharacterAsync(other));
    }

    [Fact]
    public async Task Clear_empty_position_is_a_noop()
    {
        if (!_fx.Available) return;
        var repo = new QuickSlotRepository(_fx.Db!);
        var ch = NewChar();
        await repo.SetAsync(Entry(ch, 0, 0, 1));

        await repo.ClearAsync(ch, 2, 7);
        await repo.ClearAsync(NewChar(), 0, 0);

        Assert.Equal(1u, Assert.Single(await repo.GetByCharacterAsync(ch)).EntryId);
    }

    [Fact]
    public async Task Swap_two_occupied_positions_exchanges_them()
    {
        if (!_fx.Available) return;
        var repo = new QuickSlotRepository(_fx.Db!);
        var ch = NewChar();
        await repo.SetAsync(Entry(ch, 0, 1, 111, type: 1, amount: 3, uid: 0));
        await repo.SetAsync(Entry(ch, 2, 8, 222, type: 3, amount: 7, uid: 555));

        await repo.SwapAsync(ch, 0, 1, 2, 8);

        var got = await repo.GetByCharacterAsync(ch);
        Assert.Equal(2, got.Count);
        var atA = got.Single(s => s.Page == 0 && s.Slot == 1);
        var atB = got.Single(s => s.Page == 2 && s.Slot == 8);
        Assert.Equal(222u, atA.EntryId);
        Assert.Equal((byte)3, atA.EntryType);
        Assert.Equal((ushort)7, atA.Amount);
        Assert.Equal(555UL, atA.ItemUid);
        Assert.Equal(111u, atB.EntryId);
        Assert.Equal((byte)1, atB.EntryType);
        Assert.Equal((ushort)3, atB.Amount);
        Assert.Equal(0UL, atB.ItemUid);
    }

    [Fact]
    public async Task Swap_with_empty_target_moves_the_entry()
    {
        if (!_fx.Available) return;
        var repo = new QuickSlotRepository(_fx.Db!);
        var ch = NewChar();
        await repo.SetAsync(Entry(ch, 0, 0, 42, uid: 9));

        await repo.SwapAsync(ch, 0, 0, 1, 4);

        var moved = Assert.Single(await repo.GetByCharacterAsync(ch));
        Assert.Equal((byte)1, moved.Page);
        Assert.Equal((byte)4, moved.Slot);
        Assert.Equal(42u, moved.EntryId);
        Assert.Equal(9UL, moved.ItemUid);
    }

    [Fact]
    public async Task Swap_with_empty_source_moves_the_other_entry()
    {
        if (!_fx.Available) return;
        var repo = new QuickSlotRepository(_fx.Db!);
        var ch = NewChar();
        await repo.SetAsync(Entry(ch, 1, 4, 43));

        await repo.SwapAsync(ch, 0, 0, 1, 4); 

        var moved = Assert.Single(await repo.GetByCharacterAsync(ch));
        Assert.Equal((byte)0, moved.Page);
        Assert.Equal((byte)0, moved.Slot);
        Assert.Equal(43u, moved.EntryId);
    }

    [Fact]
    public async Task Swap_two_empty_positions_changes_nothing()
    {
        if (!_fx.Available) return;
        var repo = new QuickSlotRepository(_fx.Db!);
        var ch = NewChar();
        await repo.SetAsync(Entry(ch, 2, 2, 5));

        await repo.SwapAsync(ch, 0, 0, 1, 1);

        var got = Assert.Single(await repo.GetByCharacterAsync(ch));
        Assert.Equal((byte)2, got.Page);
        Assert.Equal((byte)2, got.Slot);
        Assert.Equal(5u, got.EntryId);
    }

    [Fact]
    public async Task Swap_same_position_leaves_entry_in_place()
    {
        if (!_fx.Available) return;
        var repo = new QuickSlotRepository(_fx.Db!);
        var ch = NewChar();
        await repo.SetAsync(Entry(ch, 1, 6, 77, type: 2, amount: 4, uid: 123));

        await repo.SwapAsync(ch, 1, 6, 1, 6);

        var got = Assert.Single(await repo.GetByCharacterAsync(ch));
        Assert.Equal((byte)1, got.Page);
        Assert.Equal((byte)6, got.Slot);
        Assert.Equal(77u, got.EntryId);
        Assert.Equal((ushort)4, got.Amount);
        Assert.Equal(123UL, got.ItemUid);
    }

    [Fact]
    public async Task Swap_does_not_touch_other_slots_or_characters()
    {
        if (!_fx.Available) return;
        var repo = new QuickSlotRepository(_fx.Db!);
        var ch = NewChar();
        var other = NewChar();
        await repo.SetAsync(Entry(ch, 0, 0, 1));
        await repo.SetAsync(Entry(ch, 0, 1, 2));
        await repo.SetAsync(Entry(ch, 0, 2, 3));
        await repo.SetAsync(Entry(other, 0, 0, 90));
        await repo.SetAsync(Entry(other, 0, 1, 91));

        await repo.SwapAsync(ch, 0, 0, 0, 1);

        Assert.Equal(new[] { 2u, 1u, 3u }, (await repo.GetByCharacterAsync(ch)).Select(s => s.EntryId).ToArray());
        Assert.Equal(new[] { 90u, 91u }, (await repo.GetByCharacterAsync(other)).Select(s => s.EntryId).ToArray());
    }
}
