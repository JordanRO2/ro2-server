using RO2.Domain.Entities;
using RO2.Persistence.Postgres.Repositories;
using Xunit;

namespace RO2.Tests.Integration;

/// <summary>
/// Integration tests for <see cref="PetIncubatorRepository"/> against a live PostgreSQL instance.
/// </summary>
/// <remarks>
/// Covers place, start and collect rule outcomes, the restore compensation, per-character isolation and storage types. Each test uses a fresh random identifier so tests can share one container.
/// </remarks>
[Collection("Postgres")]
public sealed class PostgresPetIncubatorTests
{
    private static readonly DateTime T0 = new(2026, 10, 18, 12, 0, 0, 123, DateTimeKind.Utc);

    private readonly PostgresFixture _fx;
    public PostgresPetIncubatorTests(PostgresFixture fx) => _fx = fx;

    private static uint NewCharId() => (uint)Random.Shared.NextInt64(2_200_000_000L, 4_294_967_000L);

    [Fact]
    public async Task GetSlots_unknown_character_is_empty()
    {
        if (!_fx.Available) return;
        var repo = new PetIncubatorRepository(_fx.Db!);
        Assert.Empty(await repo.GetSlotsAsync(NewCharId()));
    }

    [Fact]
    public async Task Place_stores_fresh_incubator_with_no_egg()
    {
        if (!_fx.Available) return;
        var repo = new PetIncubatorRepository(_fx.Db!);
        var ch = NewCharId();
        
        Assert.True(await repo.TryPlaceIncubatorAsync(ch, 1, 3_000_000_001u, 5, T0));

        var slots = await repo.GetSlotsAsync(ch);
        var s = Assert.Single(slots);
        Assert.Equal((byte)1, s.Slot);
        Assert.Equal(3_000_000_001u, s.IncubatorId);
        Assert.Equal((byte)5, s.Charges);
        Assert.Equal(0u, s.EggId);
        Assert.Equal(T0, s.StartUtc);
        Assert.Equal(T0, s.EndUtc);
        Assert.Equal(DateTimeKind.Utc, s.StartUtc.Kind);
    }

    [Fact]
    public async Task Place_truncates_times_to_milliseconds_like_a_bson_date()
    {
        if (!_fx.Available) return;
        var repo = new PetIncubatorRepository(_fx.Db!);
        var ch = NewCharId();
        var withTicks = T0.AddTicks(4567); 
        Assert.True(await repo.TryPlaceIncubatorAsync(ch, 0, 10, 1, withTicks));
        var s = Assert.Single(await repo.GetSlotsAsync(ch));
        Assert.Equal(T0, s.StartUtc);
        Assert.Equal(T0, s.EndUtc);
    }

    [Fact]
    public async Task Place_refuses_slot_two_or_more_and_stores_nothing()
    {
        if (!_fx.Available) return;
        var repo = new PetIncubatorRepository(_fx.Db!);
        var ch = NewCharId();
        Assert.False(await repo.TryPlaceIncubatorAsync(ch, 2, 10, 3, T0));
        Assert.False(await repo.TryPlaceIncubatorAsync(ch, 255, 10, 3, T0));
        Assert.Empty(await repo.GetSlotsAsync(ch));
    }

    [Fact]
    public async Task Place_refuses_zero_charges_and_stores_nothing()
    {
        if (!_fx.Available) return;
        var repo = new PetIncubatorRepository(_fx.Db!);
        var ch = NewCharId();
        Assert.False(await repo.TryPlaceIncubatorAsync(ch, 0, 10, 0, T0));
        Assert.Empty(await repo.GetSlotsAsync(ch));
    }

    [Fact]
    public async Task Place_refuses_occupied_slot_and_keeps_the_original()
    {
        if (!_fx.Available) return;
        var repo = new PetIncubatorRepository(_fx.Db!);
        var ch = NewCharId();
        Assert.True(await repo.TryPlaceIncubatorAsync(ch, 0, 10, 3, T0));
        Assert.False(await repo.TryPlaceIncubatorAsync(ch, 0, 99, 7, T0.AddMinutes(1)));

        var s = Assert.Single(await repo.GetSlotsAsync(ch));
        Assert.Equal(10u, s.IncubatorId);
        Assert.Equal((byte)3, s.Charges);
        Assert.Equal(T0, s.StartUtc);
    }

    [Fact]
    public async Task Place_both_slots_keeps_insertion_order()
    {
        if (!_fx.Available) return;
        var repo = new PetIncubatorRepository(_fx.Db!);
        var ch = NewCharId();
        Assert.True(await repo.TryPlaceIncubatorAsync(ch, 1, 11, 2, T0));
        Assert.True(await repo.TryPlaceIncubatorAsync(ch, 0, 10, 4, T0));

        var slots = await repo.GetSlotsAsync(ch);
        Assert.Equal(2, slots.Count);
        Assert.Equal((byte)1, slots[0].Slot);
        Assert.Equal(11u, slots[0].IncubatorId);
        Assert.Equal((byte)0, slots[1].Slot);
        Assert.Equal(10u, slots[1].IncubatorId);
    }

    [Fact]
    public async Task StartIncubation_sets_egg_and_window_without_touching_charges()
    {
        if (!_fx.Available) return;
        var repo = new PetIncubatorRepository(_fx.Db!);
        var ch = NewCharId();
        Assert.True(await repo.TryPlaceIncubatorAsync(ch, 0, 10, 3, T0));
        var start = T0.AddMinutes(1);
        var end = T0.AddHours(2);
        Assert.True(await repo.TryStartIncubationAsync(ch, 0, 10, 3_100_000_000u, start, end));

        var s = Assert.Single(await repo.GetSlotsAsync(ch));
        Assert.Equal(3_100_000_000u, s.EggId);
        Assert.Equal((byte)3, s.Charges);
        Assert.Equal(10u, s.IncubatorId);
        Assert.Equal(start, s.StartUtc);
        Assert.Equal(end, s.EndUtc);
    }

    [Fact]
    public async Task StartIncubation_refuses_missing_slot_wrong_incubator_or_zero_egg()
    {
        if (!_fx.Available) return;
        var repo = new PetIncubatorRepository(_fx.Db!);
        var ch = NewCharId();
        
        Assert.False(await repo.TryStartIncubationAsync(ch, 0, 10, 500, T0, T0.AddHours(1)));
        Assert.Empty(await repo.GetSlotsAsync(ch));

        Assert.True(await repo.TryPlaceIncubatorAsync(ch, 0, 10, 3, T0));
        
        Assert.False(await repo.TryStartIncubationAsync(ch, 1, 10, 500, T0, T0.AddHours(1)));
        Assert.False(await repo.TryStartIncubationAsync(ch, 0, 11, 500, T0, T0.AddHours(1)));
        Assert.False(await repo.TryStartIncubationAsync(ch, 0, 10, 0, T0, T0.AddHours(1)));

        var s = Assert.Single(await repo.GetSlotsAsync(ch));
        Assert.Equal(0u, s.EggId);
        Assert.Equal(T0, s.EndUtc);
    }

    [Fact]
    public async Task StartIncubation_refuses_a_slot_that_already_holds_an_egg()
    {
        if (!_fx.Available) return;
        var repo = new PetIncubatorRepository(_fx.Db!);
        var ch = NewCharId();
        Assert.True(await repo.TryPlaceIncubatorAsync(ch, 0, 10, 3, T0));
        var end = T0.AddHours(1);
        Assert.True(await repo.TryStartIncubationAsync(ch, 0, 10, 500, T0, end));
        Assert.False(await repo.TryStartIncubationAsync(ch, 0, 10, 600, T0.AddMinutes(5), T0.AddHours(9)));

        var s = Assert.Single(await repo.GetSlotsAsync(ch));
        Assert.Equal(500u, s.EggId);
        Assert.Equal(end, s.EndUtc);
    }

    [Fact]
    public async Task Collect_takes_one_charge_empties_the_slot_and_returns_the_previous_state()
    {
        if (!_fx.Available) return;
        var repo = new PetIncubatorRepository(_fx.Db!);
        var ch = NewCharId();
        var end = T0.AddHours(1);
        var now = end.AddMinutes(3);
        Assert.True(await repo.TryPlaceIncubatorAsync(ch, 0, 10, 3, T0));
        Assert.True(await repo.TryStartIncubationAsync(ch, 0, 10, 500, T0, end));

        var before = await repo.TryCollectAsync(ch, 0, 10, now);
        Assert.NotNull(before);
        Assert.Equal((byte)0, before!.Slot);
        Assert.Equal(10u, before.IncubatorId);
        Assert.Equal((byte)3, before.Charges);
        Assert.Equal(500u, before.EggId);
        Assert.Equal(T0, before.StartUtc);
        Assert.Equal(end, before.EndUtc);

        var s = Assert.Single(await repo.GetSlotsAsync(ch));
        Assert.Equal((byte)2, s.Charges);
        Assert.Equal(0u, s.EggId);
        Assert.Equal(10u, s.IncubatorId);
        Assert.Equal(now, s.StartUtc);
        Assert.Equal(now, s.EndUtc);
    }

    [Fact]
    public async Task Collect_exactly_at_the_hatch_instant_is_allowed()
    {
        if (!_fx.Available) return;
        var repo = new PetIncubatorRepository(_fx.Db!);
        var ch = NewCharId();
        var end = T0.AddHours(1);
        Assert.True(await repo.TryPlaceIncubatorAsync(ch, 0, 10, 2, T0));
        Assert.True(await repo.TryStartIncubationAsync(ch, 0, 10, 500, T0, end));
        Assert.NotNull(await repo.TryCollectAsync(ch, 0, 10, end));
    }

    [Fact]
    public async Task Collect_before_hatch_is_null_and_stores_nothing()
    {
        if (!_fx.Available) return;
        var repo = new PetIncubatorRepository(_fx.Db!);
        var ch = NewCharId();
        var end = T0.AddHours(1);
        Assert.True(await repo.TryPlaceIncubatorAsync(ch, 0, 10, 2, T0));
        Assert.True(await repo.TryStartIncubationAsync(ch, 0, 10, 500, T0, end));

        Assert.Null(await repo.TryCollectAsync(ch, 0, 10, end.AddMilliseconds(-1)));

        var s = Assert.Single(await repo.GetSlotsAsync(ch));
        Assert.Equal(500u, s.EggId);
        Assert.Equal((byte)2, s.Charges);
        Assert.Equal(end, s.EndUtc);
    }

    [Fact]
    public async Task Collect_refuses_missing_slot_wrong_incubator_and_empty_incubator()
    {
        if (!_fx.Available) return;
        var repo = new PetIncubatorRepository(_fx.Db!);
        var ch = NewCharId();
        var now = T0.AddDays(1);
        
        Assert.Null(await repo.TryCollectAsync(ch, 0, 10, now));
        Assert.Empty(await repo.GetSlotsAsync(ch));

        Assert.True(await repo.TryPlaceIncubatorAsync(ch, 0, 10, 2, T0));
        
        Assert.Null(await repo.TryCollectAsync(ch, 0, 10, now));
        Assert.True(await repo.TryStartIncubationAsync(ch, 0, 10, 500, T0, T0.AddHours(1)));
        
        Assert.Null(await repo.TryCollectAsync(ch, 1, 10, now));
        Assert.Null(await repo.TryCollectAsync(ch, 0, 11, now));

        var s = Assert.Single(await repo.GetSlotsAsync(ch));
        Assert.Equal(500u, s.EggId);
        Assert.Equal((byte)2, s.Charges);
    }

    [Fact]
    public async Task Collect_of_the_last_charge_removes_the_slot_and_frees_it_for_a_new_incubator()
    {
        if (!_fx.Available) return;
        var repo = new PetIncubatorRepository(_fx.Db!);
        var ch = NewCharId();
        var end = T0.AddHours(1);
        Assert.True(await repo.TryPlaceIncubatorAsync(ch, 0, 10, 1, T0));
        Assert.True(await repo.TryStartIncubationAsync(ch, 0, 10, 500, T0, end));

        var before = await repo.TryCollectAsync(ch, 0, 10, end);
        Assert.NotNull(before);
        Assert.Equal((byte)1, before!.Charges);
        Assert.Empty(await repo.GetSlotsAsync(ch));

        
        Assert.True(await repo.TryPlaceIncubatorAsync(ch, 0, 20, 4, end));
        var s = Assert.Single(await repo.GetSlotsAsync(ch));
        Assert.Equal(20u, s.IncubatorId);
        Assert.Equal((byte)4, s.Charges);
    }

    [Fact]
    public async Task Restore_puts_back_a_removed_slot()
    {
        if (!_fx.Available) return;
        var repo = new PetIncubatorRepository(_fx.Db!);
        var ch = NewCharId();
        var end = T0.AddHours(1);
        Assert.True(await repo.TryPlaceIncubatorAsync(ch, 0, 10, 1, T0));
        Assert.True(await repo.TryStartIncubationAsync(ch, 0, 10, 500, T0, end));
        var before = await repo.TryCollectAsync(ch, 0, 10, end);
        Assert.NotNull(before);
        Assert.Empty(await repo.GetSlotsAsync(ch));

        await repo.RestoreSlotAsync(ch, before!);

        var s = Assert.Single(await repo.GetSlotsAsync(ch));
        Assert.Equal((byte)0, s.Slot);
        Assert.Equal(10u, s.IncubatorId);
        Assert.Equal((byte)1, s.Charges);
        Assert.Equal(500u, s.EggId);
        Assert.Equal(T0, s.StartUtc);
        Assert.Equal(end, s.EndUtc);
        
        Assert.NotNull(await repo.TryCollectAsync(ch, 0, 10, end));
    }

    [Fact]
    public async Task Restore_replaces_the_current_slot_and_moves_it_to_the_end_of_the_list()
    {
        if (!_fx.Available) return;
        var repo = new PetIncubatorRepository(_fx.Db!);
        var ch = NewCharId();
        var end = T0.AddHours(1);
        Assert.True(await repo.TryPlaceIncubatorAsync(ch, 0, 10, 3, T0));
        Assert.True(await repo.TryPlaceIncubatorAsync(ch, 1, 11, 2, T0));
        Assert.True(await repo.TryStartIncubationAsync(ch, 0, 10, 500, T0, end));
        var before = await repo.TryCollectAsync(ch, 0, 10, end); 
        Assert.NotNull(before);

        await repo.RestoreSlotAsync(ch, before!);

        var slots = await repo.GetSlotsAsync(ch);
        Assert.Equal(2, slots.Count);
        Assert.Equal((byte)1, slots[0].Slot);          
        Assert.Equal((byte)0, slots[1].Slot);          
        Assert.Equal((byte)3, slots[1].Charges);       
        Assert.Equal(500u, slots[1].EggId);
        Assert.Equal(end, slots[1].EndUtc);
    }

    [Fact]
    public async Task Restore_on_a_character_with_no_row_creates_it()
    {
        if (!_fx.Available) return;
        var repo = new PetIncubatorRepository(_fx.Db!);
        var ch = NewCharId();
        await repo.RestoreSlotAsync(ch, new PetIncubatorSlot
        {
            Slot = 1, IncubatorId = 42, Charges = 2, EggId = 7, StartUtc = T0, EndUtc = T0.AddHours(1),
        });

        var s = Assert.Single(await repo.GetSlotsAsync(ch));
        Assert.Equal((byte)1, s.Slot);
        Assert.Equal(42u, s.IncubatorId);
        Assert.Equal((byte)2, s.Charges);
        Assert.Equal(7u, s.EggId);
        Assert.Equal(T0.AddHours(1), s.EndUtc);
    }

    [Fact]
    public async Task Characters_are_isolated_from_each_other()
    {
        if (!_fx.Available) return;
        var repo = new PetIncubatorRepository(_fx.Db!);
        var a = NewCharId();
        var b = a + 1;
        Assert.True(await repo.TryPlaceIncubatorAsync(a, 0, 10, 3, T0));
        Assert.Empty(await repo.GetSlotsAsync(b));
        Assert.True(await repo.TryPlaceIncubatorAsync(b, 0, 20, 1, T0));

        Assert.Equal(10u, Assert.Single(await repo.GetSlotsAsync(a)).IncubatorId);
        Assert.Equal(20u, Assert.Single(await repo.GetSlotsAsync(b)).IncubatorId);
    }
}
