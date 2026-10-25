using Npgsql;
using RO2.Domain.Entities;
using RO2.Persistence.Postgres.Repositories;
using Xunit;
using RO2.Inventory.Domain;

namespace RO2.Tests.Integration;

/// <summary>
/// Integration tests for <see cref="InventoryRepository"/> against a live PostgreSQL instance.
/// </summary>
/// <remarks>
/// Covers id allocation, add/get round-trips, slot selection, remove/adjust, update, storage filtering and slot repair. Each test uses a fresh random identifier so tests can share one container.
/// </remarks>
[Collection("Postgres")]
public sealed class PostgresInventoryTests
{
    private readonly PostgresFixture _fx;
    public PostgresInventoryTests(PostgresFixture fx) => _fx = fx;

    private static long _keys = 3_000_000_000L + Random.Shared.Next(0, 100_000_000);
    private static uint NewChar() => (uint)Interlocked.Increment(ref _keys);

    
    private static long _presetIds = 0x4000_0000_0000_0000L + DateTime.UtcNow.Ticks % 1_000_000_000L;
    private static ulong PresetId() => (ulong)Interlocked.Increment(ref _presetIds);

    private static InventoryItem NewItem(uint charId, short slot = -1, byte storage = 0, uint itemId = 5000, int count = 1) =>
        new() { CharacterId = charId, ItemId = itemId, Count = count, Slot = slot, StorageType = storage };

    [Fact]
    public async Task GetNextId_is_monotonic_and_unique()
    {
        if (!_fx.Available) return;
        var repo = new InventoryRepository(_fx.Db!);

        var a = await repo.GetNextIdAsync();
        var b = await repo.GetNextIdAsync();
        Assert.True(a > 0);
        Assert.Equal(a + 1, b);

        var ids = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => repo.GetNextIdAsync()));
        Assert.Equal(10, ids.Distinct().Count());
        Assert.All(ids, id => Assert.True(id > b));
    }

    [Fact]
    public async Task AddItem_assigns_an_id_and_round_trips_every_field()
    {
        if (!_fx.Available) return;
        var repo = new InventoryRepository(_fx.Db!);
        var ch = NewChar();
        var created = new DateTime(2026, 3, 4, 5, 6, 7, 89, DateTimeKind.Utc);
        var item = new InventoryItem
        {
            CharacterId = ch,
            ItemId = uint.MaxValue - 1,
            Count = 250,
            Slot = 7,
            StorageType = 2,
            Refine = 9,
            Element = 3,
            Durability = 77,
            DyeColor = 0xFF272725u,
            DyeColor2 = 0xFFFFFFFFu,
            DyeColor3 = 0x80000000u,
            FxEnchantIndex = uint.MaxValue,
            EnchantReinforceId = ushort.MaxValue,
            EnchantSeconds = 40000,
            CardsJson = "[1,2,3]",
            SocketPunchersJson = "[4,5]",
            Bound = true,
            AbilityValue = uint.MaxValue,
            CreatedAt = created,
        };

        await repo.AddItemAsync(item);
        Assert.NotEqual(0UL, item.Id);

        var got = await repo.GetByIdAsync(item.Id);
        Assert.NotNull(got);
        Assert.Equal(item.Id, got.Id);
        Assert.Equal(ch, got.CharacterId);
        Assert.Equal(uint.MaxValue - 1, got.ItemId);
        Assert.Equal(250, got.Count);
        Assert.Equal((short)7, got.Slot);
        Assert.Equal((byte)2, got.StorageType);
        Assert.Equal((byte)9, got.Refine);
        Assert.Equal((byte)3, got.Element);
        Assert.Equal((short)77, got.Durability);
        Assert.Equal(0xFF272725u, got.DyeColor);
        Assert.Equal(0xFFFFFFFFu, got.DyeColor2);
        Assert.Equal(0x80000000u, got.DyeColor3);
        Assert.Equal(uint.MaxValue, got.FxEnchantIndex);
        Assert.Equal(ushort.MaxValue, got.EnchantReinforceId);
        Assert.Equal((ushort)40000, got.EnchantSeconds);
        Assert.Equal("[1,2,3]", got.CardsJson);
        Assert.Equal("[4,5]", got.SocketPunchersJson);
        Assert.True(got.Bound);
        Assert.Equal(uint.MaxValue, got.AbilityValue);
        Assert.Equal(created, got.CreatedAt.ToUniversalTime());
    }

    [Fact]
    public async Task Defaults_and_nulls_round_trip()
    {
        if (!_fx.Available) return;
        var repo = new InventoryRepository(_fx.Db!);
        var item = NewItem(NewChar(), slot: 0);

        await repo.AddItemAsync(item);

        var got = await repo.GetByIdAsync(item.Id);
        Assert.NotNull(got);
        Assert.Equal((short)-1, got.Durability);
        Assert.Null(got.CardsJson);
        Assert.Null(got.SocketPunchersJson);
        Assert.Null(got.AbilityValue);
        Assert.False(got.Bound);
        Assert.Equal(0u, got.DyeColor);
    }

    [Fact]
    public async Task Ids_above_int_max_round_trip()
    {
        if (!_fx.Available) return;
        var repo = new InventoryRepository(_fx.Db!);
        var ch = NewChar();
        var id = 0xE043DA7F8471958UL; 
        var item = NewItem(ch, slot: 1);
        item.Id = id;

        await repo.AddItemAsync(item);

        Assert.True(ch > int.MaxValue);
        var got = await repo.GetByIdAsync(id);
        Assert.NotNull(got);
        Assert.Equal(id, got.Id);
        Assert.Equal(ch, got.CharacterId);
        Assert.Equal(id, Assert.Single(await repo.GetByCharacterAsync(ch)).Id);
        Assert.True(await repo.RemoveItemAsync(id, ch));
        Assert.Null(await repo.GetByIdAsync(id));
    }

    [Fact]
    public async Task GetById_returns_null_when_missing()
    {
        if (!_fx.Available) return;
        var repo = new InventoryRepository(_fx.Db!);

        Assert.Null(await repo.GetByIdAsync(PresetId()));
    }

    [Fact]
    public async Task AddItem_with_explicit_slot_keeps_that_slot_and_preset_id()
    {
        if (!_fx.Available) return;
        var repo = new InventoryRepository(_fx.Db!);
        var item = NewItem(NewChar(), slot: 12);
        item.Id = PresetId();
        var preset = item.Id;

        await repo.AddItemAsync(item);

        Assert.Equal(preset, item.Id);
        var got = await repo.GetByIdAsync(preset);
        Assert.NotNull(got);
        Assert.Equal((short)12, got.Slot);
    }

    [Fact]
    public async Task AddItem_picks_the_lowest_free_slot()
    {
        if (!_fx.Available) return;
        var repo = new InventoryRepository(_fx.Db!);
        var ch = NewChar();
        await repo.AddItemAsync(NewItem(ch, slot: 0));
        await repo.AddItemAsync(NewItem(ch, slot: 1));
        await repo.AddItemAsync(NewItem(ch, slot: 3));

        var a = NewItem(ch);
        await repo.AddItemAsync(a); 
        var b = NewItem(ch);
        await repo.AddItemAsync(b); 

        Assert.Equal((short)2, a.Slot);
        Assert.Equal((short)4, b.Slot);
        Assert.Equal((short)2, (await repo.GetByIdAsync(a.Id))!.Slot);
        Assert.Equal(new short[] { 0, 1, 2, 3, 4 }, (await repo.GetByCharacterAsync(ch)).Select(i => i.Slot).ToArray());
    }

    [Fact]
    public async Task Lowest_free_slot_is_per_storage_type_and_per_character()
    {
        if (!_fx.Available) return;
        var repo = new InventoryRepository(_fx.Db!);
        var ch = NewChar();
        var other = NewChar();
        await repo.AddItemAsync(NewItem(ch, slot: 0, storage: 0));
        await repo.AddItemAsync(NewItem(ch, slot: 1, storage: 0));

        var storage = NewItem(ch, storage: 2);
        await repo.AddItemAsync(storage);
        var theirs = NewItem(other);
        await repo.AddItemAsync(theirs);

        Assert.Equal((short)0, storage.Slot);
        Assert.Equal((short)0, theirs.Slot);
    }

    [Fact]
    public void LowestFreeSlot_helper()
    {
        Assert.Equal((short)0, InventoryRepository.LowestFreeSlot([]));
        Assert.Equal((short)2, InventoryRepository.LowestFreeSlot([0, 1, 5, -1]));
        Assert.Equal((short)0, InventoryRepository.LowestFreeSlot([-1, 3]));
    }

    [Fact]
    public async Task Concurrent_adds_land_in_distinct_slots_via_the_collision_retry()
    {
        if (!_fx.Available) return;
        var repo = new InventoryRepository(_fx.Db!);
        var ch = NewChar();
        
        var items = Enumerable.Range(0, 5).Select(_ => NewItem(ch)).ToArray();

        await Task.WhenAll(items.Select(i => repo.AddItemAsync(i)));

        Assert.Equal(5, items.Select(i => i.Slot).Distinct().Count());
        var rows = await repo.GetByCharacterAsync(ch);
        Assert.Equal(5, rows.Count);
        Assert.Equal(new short[] { 0, 1, 2, 3, 4 }, rows.Select(r => r.Slot).ToArray());
        Assert.Equal(5, rows.Select(r => r.Id).Distinct().Count());
    }

    [Fact]
    public async Task Two_items_into_the_same_storage_get_distinct_slots()
    {
        if (!_fx.Available) return;
        var repo = new InventoryRepository(_fx.Db!);
        var ch = NewChar();
        var a = NewItem(ch);
        var b = NewItem(ch);

        await repo.AddItemAsync(a);
        await repo.AddItemAsync(b);

        Assert.NotEqual(a.Slot, b.Slot);
        Assert.Equal((short)0, a.Slot);
        Assert.Equal((short)1, b.Slot);
    }

    [Fact]
    public async Task Forced_duplicate_slot_is_rejected_by_the_unique_index()
    {
        if (!_fx.Available) return;
        var repo = new InventoryRepository(_fx.Db!);
        var ch = NewChar();
        await repo.AddItemAsync(NewItem(ch, slot: 5));

        var ex = await Assert.ThrowsAsync<PostgresException>(() => repo.AddItemAsync(NewItem(ch, slot: 5)));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, ex.SqlState);
        Assert.Equal(InventoryRepository.SlotIndexName, ex.ConstraintName);
        Assert.Single(await repo.GetByCharacterAsync(ch));
        
        await repo.AddItemAsync(NewItem(ch, slot: 5, storage: 2));
    }

    [Fact]
    public async Task Duplicate_item_id_propagates_and_is_not_retried_as_a_slot_collision()
    {
        if (!_fx.Available) return;
        var repo = new InventoryRepository(_fx.Db!);
        var ch = NewChar();
        var id = PresetId();
        var first = NewItem(ch, slot: 0);
        first.Id = id;
        await repo.AddItemAsync(first);

        var dup = NewItem(ch);
        dup.Id = id;
        var ex = await Assert.ThrowsAsync<PostgresException>(() => repo.AddItemAsync(dup));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, ex.SqlState);
        Assert.NotEqual(InventoryRepository.SlotIndexName, ex.ConstraintName);
    }

    [Fact]
    public async Task Unplaced_slot_minus_one_rows_may_coexist_when_inserted_directly()
    {
        if (!_fx.Available) return;
        var repo = new InventoryRepository(_fx.Db!);
        var ch = NewChar();
        
        
        var a = NewItem(ch);
        var b = NewItem(ch);
        await repo.AddItemAsync(a);
        await repo.AddItemAsync(b);
        a.Slot = -1;
        b.Slot = -1;
        await repo.UpdateItemAsync(a);
        await repo.UpdateItemAsync(b);

        Assert.All(await repo.GetByCharacterAsync(ch), r => Assert.Equal((short)-1, r.Slot));
        Assert.Equal(2, await repo.RepairSlotsAsync(ch));
        Assert.Equal(new short[] { 0, 1 }, (await repo.GetByCharacterAsync(ch)).Select(r => r.Slot).ToArray());
    }

    [Fact]
    public async Task GetByCharacter_orders_by_slot_and_filters_by_character()
    {
        if (!_fx.Available) return;
        var repo = new InventoryRepository(_fx.Db!);
        var ch = NewChar();
        await repo.AddItemAsync(NewItem(ch, slot: 4, itemId: 1));
        await repo.AddItemAsync(NewItem(ch, slot: 1, itemId: 2));
        await repo.AddItemAsync(NewItem(ch, slot: 2, storage: 1, itemId: 3));
        await repo.AddItemAsync(NewItem(NewChar(), slot: 0, itemId: 4));

        var rows = await repo.GetByCharacterAsync(ch);

        Assert.Equal(3, rows.Count);
        Assert.Equal(new short[] { 1, 2, 4 }, rows.Select(r => r.Slot).ToArray());
        Assert.Empty(await repo.GetByCharacterAsync(NewChar()));
    }

    [Fact]
    public async Task GetEquipment_returns_only_storage_type_1()
    {
        if (!_fx.Available) return;
        var repo = new InventoryRepository(_fx.Db!);
        var ch = NewChar();
        await repo.AddItemAsync(NewItem(ch, slot: 0, storage: 0, itemId: 10));
        await repo.AddItemAsync(NewItem(ch, slot: 3, storage: 1, itemId: 11));
        await repo.AddItemAsync(NewItem(ch, slot: 0, storage: 1, itemId: 12));
        await repo.AddItemAsync(NewItem(ch, slot: 0, storage: 2, itemId: 13));

        var equip = await repo.GetEquipmentAsync(ch);

        Assert.Equal(2, equip.Count);
        Assert.All(equip, e => Assert.Equal((byte)1, e.StorageType));
        Assert.Equal(new uint[] { 12, 11 }, equip.Select(e => e.ItemId).ToArray());
    }

    [Fact]
    public async Task UpdateItem_writes_every_mutable_field_and_keeps_created_at()
    {
        if (!_fx.Available) return;
        var repo = new InventoryRepository(_fx.Db!);
        var ch = NewChar();
        var created = new DateTime(2025, 1, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        var item = NewItem(ch, slot: 2);
        item.CreatedAt = created;
        await repo.AddItemAsync(item);

        var newOwner = NewChar();
        item.CharacterId = newOwner; 
        item.ItemId = 4_000_000_000u;
        item.Count = 9;
        item.Slot = 6;
        item.StorageType = 2;
        item.Refine = 4;
        item.Element = 2;
        item.Durability = 12;
        item.DyeColor = 0xFF010203u;
        item.DyeColor2 = 5;
        item.DyeColor3 = 6;
        item.FxEnchantIndex = 3_000_000_000u;
        item.EnchantReinforceId = 60000;
        item.EnchantSeconds = 59999;
        item.CardsJson = "[7]";
        item.SocketPunchersJson = "[8]";
        item.Bound = true;
        item.AbilityValue = 0;
        item.CreatedAt = DateTime.UtcNow; 
        await repo.UpdateItemAsync(item);

        var got = await repo.GetByIdAsync(item.Id);
        Assert.NotNull(got);
        Assert.Equal(newOwner, got.CharacterId);
        Assert.Equal(4_000_000_000u, got.ItemId);
        Assert.Equal(9, got.Count);
        Assert.Equal((short)6, got.Slot);
        Assert.Equal((byte)2, got.StorageType);
        Assert.Equal((byte)4, got.Refine);
        Assert.Equal((byte)2, got.Element);
        Assert.Equal((short)12, got.Durability);
        Assert.Equal(0xFF010203u, got.DyeColor);
        Assert.Equal(5u, got.DyeColor2);
        Assert.Equal(6u, got.DyeColor3);
        Assert.Equal(3_000_000_000u, got.FxEnchantIndex);
        Assert.Equal((ushort)60000, got.EnchantReinforceId);
        Assert.Equal((ushort)59999, got.EnchantSeconds);
        Assert.Equal("[7]", got.CardsJson);
        Assert.Equal("[8]", got.SocketPunchersJson);
        Assert.True(got.Bound);
        Assert.Equal(0u, got.AbilityValue);
        Assert.Equal(created, got.CreatedAt.ToUniversalTime());
        Assert.Empty(await repo.GetByCharacterAsync(ch));

        
        item.CardsJson = null;
        item.AbilityValue = null;
        await repo.UpdateItemAsync(item);
        got = await repo.GetByIdAsync(item.Id);
        Assert.NotNull(got);
        Assert.Null(got.CardsJson);
        Assert.Null(got.AbilityValue);
    }

    [Fact]
    public async Task UpdateItem_of_a_missing_row_is_a_no_op()
    {
        if (!_fx.Available) return;
        var repo = new InventoryRepository(_fx.Db!);
        var ghost = NewItem(NewChar(), slot: 1);
        ghost.Id = PresetId();

        await repo.UpdateItemAsync(ghost);

        Assert.Null(await repo.GetByIdAsync(ghost.Id));
    }

    [Fact]
    public async Task UpdateItem_swaps_slots_through_a_free_cell()
    {
        if (!_fx.Available) return;
        var repo = new InventoryRepository(_fx.Db!);
        var ch = NewChar();
        var a = NewItem(ch, slot: 0);
        var b = NewItem(ch, slot: 1);
        await repo.AddItemAsync(a);
        await repo.AddItemAsync(b);

        
        a.Slot = 1;
        await Assert.ThrowsAsync<PostgresException>(() => repo.UpdateItemAsync(a));

        a.Slot = 9;
        await repo.UpdateItemAsync(a);
        b.Slot = 0;
        await repo.UpdateItemAsync(b);
        a.Slot = 1;
        await repo.UpdateItemAsync(a);

        Assert.Equal((short)1, (await repo.GetByIdAsync(a.Id))!.Slot);
        Assert.Equal((short)0, (await repo.GetByIdAsync(b.Id))!.Slot);
    }

    [Fact]
    public async Task RemoveItem_by_id_deletes_the_row()
    {
        if (!_fx.Available) return;
        var repo = new InventoryRepository(_fx.Db!);
        var ch = NewChar();
        var keep = NewItem(ch, slot: 0);
        var drop = NewItem(ch, slot: 1);
        await repo.AddItemAsync(keep);
        await repo.AddItemAsync(drop);

        await repo.RemoveItemAsync(drop.Id);
        await repo.RemoveItemAsync(drop.Id); 

        Assert.Null(await repo.GetByIdAsync(drop.Id));
        Assert.Equal(keep.Id, Assert.Single(await repo.GetByCharacterAsync(ch)).Id);
    }

    [Fact]
    public async Task RemoveItem_with_owner_only_deletes_the_owners_row()
    {
        if (!_fx.Available) return;
        var repo = new InventoryRepository(_fx.Db!);
        var ch = NewChar();
        var item = NewItem(ch, slot: 0);
        await repo.AddItemAsync(item);

        Assert.False(await repo.RemoveItemAsync(item.Id, NewChar()));
        Assert.NotNull(await repo.GetByIdAsync(item.Id));
        Assert.True(await repo.RemoveItemAsync(item.Id, ch));
        Assert.Null(await repo.GetByIdAsync(item.Id));
        Assert.False(await repo.RemoveItemAsync(item.Id, ch));
    }

    [Fact]
    public async Task AdjustCount_adds_and_subtracts_atomically()
    {
        if (!_fx.Available) return;
        var repo = new InventoryRepository(_fx.Db!);
        var ch = NewChar();
        var item = NewItem(ch, slot: 0, count: 10);
        await repo.AddItemAsync(item);

        Assert.Equal(15, await repo.AdjustCountAsync(item.Id, ch, 5));
        Assert.Equal(15 - 15, await repo.AdjustCountAsync(item.Id, ch, -15)); 
        Assert.Equal(0, (await repo.GetByIdAsync(item.Id))!.Count);
        Assert.Equal(3, await repo.AdjustCountAsync(item.Id, ch, 3));
    }

    [Fact]
    public async Task AdjustCount_returns_null_and_changes_nothing_when_guarded()
    {
        if (!_fx.Available) return;
        var repo = new InventoryRepository(_fx.Db!);
        var ch = NewChar();
        var item = NewItem(ch, slot: 0, count: 4);
        await repo.AddItemAsync(item);

        Assert.Null(await repo.AdjustCountAsync(item.Id, ch, -5));           
        Assert.Null(await repo.AdjustCountAsync(item.Id, NewChar(), 1));     
        Assert.Null(await repo.AdjustCountAsync(item.Id, NewChar(), -1));
        Assert.Null(await repo.AdjustCountAsync(PresetId(), ch, 1));         
        Assert.Equal(4, (await repo.GetByIdAsync(item.Id))!.Count);
    }

    [Fact]
    public async Task AdjustCount_concurrent_decrements_never_overdraw()
    {
        if (!_fx.Available) return;
        var repo = new InventoryRepository(_fx.Db!);
        var ch = NewChar();
        var item = NewItem(ch, slot: 0, count: 6);
        await repo.AddItemAsync(item);

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => repo.AdjustCountAsync(item.Id, ch, -1)));

        Assert.Equal(6, results.Count(r => r is not null));
        Assert.Equal(0, (await repo.GetByIdAsync(item.Id))!.Count);
    }

    [Fact]
    public async Task EnsureIndexes_is_idempotent_and_true()
    {
        if (!_fx.Available) return;
        var repo = new InventoryRepository(_fx.Db!);

        Assert.True(await repo.EnsureIndexesAsync());
        Assert.True(await repo.EnsureIndexesAsync());
    }

    [Fact]
    public async Task RepairSlots_assigns_slots_to_unplaced_rows_and_leaves_equipment_alone()
    {
        if (!_fx.Available) return;
        var repo = new InventoryRepository(_fx.Db!);
        var ch = NewChar();
        var keeper = NewItem(ch, slot: 1);
        var unplaced1 = NewItem(ch, slot: 0);
        var unplaced2 = NewItem(ch, slot: 2);
        var equip = NewItem(ch, slot: 5, storage: 1);
        foreach (var i in new[] { keeper, unplaced1, unplaced2, equip }) await repo.AddItemAsync(i);
        unplaced1.Slot = -1;
        unplaced2.Slot = -1;
        equip.Slot = -1; 
        await repo.UpdateItemAsync(unplaced1);
        await repo.UpdateItemAsync(unplaced2);
        await repo.UpdateItemAsync(equip);

        var repaired = await repo.RepairSlotsAsync(ch);

        Assert.Equal(2, repaired);
        Assert.Equal((short)1, (await repo.GetByIdAsync(keeper.Id))!.Slot);
        var bag = (await repo.GetByCharacterAsync(ch)).Where(r => r.StorageType == 0).Select(r => r.Slot).Order().ToArray();
        Assert.Equal(new short[] { 0, 1, 2 }, bag);
        Assert.Equal((short)-1, (await repo.GetByIdAsync(equip.Id))!.Slot);
        Assert.Equal(0, await repo.RepairSlotsAsync(ch)); 
        Assert.Equal(0, await repo.RepairSlotsAsync(NewChar()));
    }
}
