using RO2.Domain.Entities;
using RO2.Network.Protocol;
using RO2.Server.Zone.Services;
using Xunit;
using static RO2.Tests.CollectionCharacterization.CollData;
using static RO2.Tests.Packets.ReauditShared;

namespace RO2.Tests.CollectionCharacterization;

/// <summary>
/// B10 S1: the incubator RMIs (1630 place an incubator, 1631 put an egg in, 1632 collect the hatched egg) driven through the REAL
/// handlers and <see cref="PetIncubatorStore"/> over a loopback session. Every answer is one 2926 whose result is always 0: a refusal
/// changes nothing and still answers the CURRENT list. A store that cannot be read answers nothing. Times are compared by window, not by value.
/// </summary>
public class PetIncubatorHandlerCharacterizationTests
{
    private static readonly ushort Op2926 = ZoneS2COpcodes.AnsPCollectionIncubator;

    private static byte[] Place(byte cell, uint incubatorId, byte target, byte bag = 1)
    {
        var b = new byte[8];
        b[0] = bag; b[1] = cell;
        BitConverter.GetBytes(incubatorId).CopyTo(b, 3);
        b[7] = target;
        return b;
    }

    private static byte[] EggIn(byte cell, uint eggId, uint incubatorId, byte target, byte bag = 1)
    {
        var b = new byte[12];
        b[0] = bag; b[1] = cell;
        BitConverter.GetBytes(eggId).CopyTo(b, 3);
        BitConverter.GetBytes(incubatorId).CopyTo(b, 7);
        b[11] = target;
        return b;
    }

    private static byte[] Collect(uint incubatorId, byte target)
    {
        var b = new byte[5];
        BitConverter.GetBytes(incubatorId).CopyTo(b, 0);
        b[4] = target;
        return b;
    }

    private static PetIncubatorSlot Slot(byte slot, uint incubator, byte charges, uint egg = 0, DateTime? start = null, DateTime? end = null)
        => new() { Slot = slot, IncubatorId = incubator, Charges = charges, EggId = egg, StartUtc = start ?? DateTime.UtcNow.AddHours(-2), EndUtc = end ?? start ?? DateTime.UtcNow.AddHours(-2) };

    private static (uint, uint, byte, byte)[] List(List<(ushort Op, byte[] Body)> frames, int at = 0)
    {
        Assert.Equal(Op2926, frames[at].Op);
        return IncubatorList.Parse(frames[at].Body);
    }

    // ------------------------------------------------------------------------------------------------ 1630 place

    [Fact]
    public async Task Placing_an_incubator_takes_one_from_the_stack_stores_the_empty_slot_with_the_tables_charges_and_answers_2329_then_2926()
    {
        await using var r = await CollRig.StartAsync();
        var item = r.Give(Incubator, 3, 0);
        var before = DateTime.UtcNow;

        var frames = await r.CallAsync(r.Handlers.Pet_1630, Place(CellOf(0), Incubator, 1));

        Assert.Equal(new ushort[] { ZoneS2COpcodes.OnUseItem, Op2926 }, frames.Select(f => f.Op).ToArray());
        Assert.Equal(Hex(r.Use2329(item, 0, 2).Item2), Hex(frames[0].Body));
        Assert.Equal(new[] { (Incubator, 0u, (byte)1, (byte)2) }, List(frames, 1));      // Charge_Quantity 2 of the table row
        var s = Assert.Single(r.Incubators.Stored(CollRig.Self));
        Assert.Equal((byte)1, s.Slot);
        Assert.InRange(s.StartUtc, before.AddSeconds(-1), DateTime.UtcNow.AddSeconds(1));
        Assert.Equal(s.StartUtc, s.EndUtc);                                                   // an empty slot
        Assert.Equal(2, r.CountOf(Incubator));
    }

    [Fact]
    public async Task Placing_the_last_incubator_of_a_stack_empties_the_cell_with_remaining_0()
    {
        await using var r = await CollRig.StartAsync();
        var item = r.Give(Incubator, 1, 0);

        var frames = await r.CallAsync(r.Handlers.Pet_1630, Place(CellOf(0), Incubator, 0));

        Assert.Equal(Hex(r.Use2329(item, 0, 0).Item2), Hex(frames[0].Body));
        Assert.Null(r.Cellrow(0));
    }

    [Fact]
    public async Task Every_1630_refusal_answers_one_2926_with_the_current_list_and_takes_nothing()
    {
        await using var r = await CollRig.StartAsync();
        r.Give(Incubator, 3, 0);
        r.Give(Filler, 1, 1);
        r.Incubators.Seed(CollRig.Self, Slot(0, IncubatorBig, 1));                  // slot 0 occupied by another incubator

        var current = new[] { (IncubatorBig, 0u, (byte)0, (byte)1) };
        Assert.Equal(current, List(await r.CallAsync(r.Handlers.Pet_1630, Place(CellOf(0), Incubator, 0))));         // occupied slot
        Assert.Equal(current, List(await r.CallAsync(r.Handlers.Pet_1630, Place(CellOf(0), Incubator, 2))));         // slot index 2: the client has only 0 and 1
        Assert.Equal(current, List(await r.CallAsync(r.Handlers.Pet_1630, Place(CellOf(0), 99999, 1))));             // not an incubator row
        Assert.Equal(current, List(await r.CallAsync(r.Handlers.Pet_1630, Place(CellOf(1), Incubator, 1))));         // the cell holds another item
        Assert.Equal(current, List(await r.CallAsync(r.Handlers.Pet_1630, Place(CellOf(5), Incubator, 1))));         // the cell is empty
        Assert.Equal(current, List(await r.CallAsync(r.Handlers.Pet_1630, Place(70, Incubator, 1))));                // not a bag cell
        Assert.Equal(3, r.CountOf(Incubator));
        Assert.Single(r.Incubators.Stored(CollRig.Self));
    }

    [Fact]
    public async Task A_short_1630_body_is_not_answered_and_an_unreadable_incubator_store_answers_nothing_either()
    {
        await using var r = await CollRig.StartAsync();
        r.Give(Incubator, 3, 0);

        Assert.Empty(await r.CallAsync(r.Handlers.Pet_1630, new byte[7]));

        r.Incubators.ThrowOnGet = true;
        Assert.Empty(await r.CallAsync(r.Handlers.Pet_1630, Place(CellOf(0), Incubator, 0)));                       // a null list returns without a reply
        Assert.Equal(3, r.CountOf(Incubator));
    }

    [Fact]
    public async Task A_failed_incubator_write_is_a_refusal_the_old_list_is_answered_and_nothing_is_consumed()
    {
        await using var r = await CollRig.StartAsync();
        r.Give(Incubator, 3, 0);
        r.Incubators.ThrowOnWrite = true;

        var frames = await r.CallAsync(r.Handlers.Pet_1630, Place(CellOf(0), Incubator, 0));

        Assert.Equal(new ushort[] { Op2926 }, frames.Select(f => f.Op).ToArray());
        Assert.Empty(List(frames));
        Assert.Equal(3, r.CountOf(Incubator));
    }

    // ------------------------------------------------------------------------------------------------ 1631 egg in

    [Fact]
    public async Task Putting_an_egg_in_takes_one_starts_incubation_for_speed_times_incubation_time_and_keeps_the_charges()
    {
        await using var r = await CollRig.StartAsync();
        var egg = r.Give(PetEgg, 2, 0);
        r.Incubators.Seed(CollRig.Self, Slot(0, Incubator, 2));
        var before = DateTime.UtcNow;

        var frames = await r.CallAsync(r.Handlers.Pet_1631, EggIn(CellOf(0), PetEgg, Incubator, 0));

        Assert.Equal(new ushort[] { ZoneS2COpcodes.OnUseItem, Op2926 }, frames.Select(f => f.Op).ToArray());
        Assert.Equal(Hex(r.Use2329(egg, 0, 1).Item2), Hex(frames[0].Body));
        Assert.Equal(new[] { (Incubator, PetEgg, (byte)0, (byte)2) }, List(frames, 1));
        var s = Assert.Single(r.Incubators.Stored(CollRig.Self));
        Assert.InRange(s.StartUtc, before.AddSeconds(-1), DateTime.UtcNow.AddSeconds(1));
        Assert.Equal(TimeSpan.FromSeconds(600), s.EndUtc - s.StartUtc);                  // 10000 * 600 / 10000
        Assert.Equal(1, r.CountOf(PetEgg));
    }

    [Fact]
    public async Task The_faster_incubator_halves_the_hatch_window()
    {
        await using var r = await CollRig.StartAsync();
        r.Give(PetEgg, 1, 0);
        r.Incubators.Seed(CollRig.Self, Slot(1, IncubatorBig, 1));

        await r.CallAsync(r.Handlers.Pet_1631, EggIn(CellOf(0), PetEgg, IncubatorBig, 1));

        var s = Assert.Single(r.Incubators.Stored(CollRig.Self));
        Assert.Equal(TimeSpan.FromSeconds(300), s.EndUtc - s.StartUtc);                  // 5000 * 600 / 10000
        Assert.Null(r.Cellrow(0));
    }

    [Fact]
    public async Task Every_1631_refusal_answers_one_2926_with_the_current_list_and_takes_nothing()
    {
        await using var r = await CollRig.StartAsync();
        r.Give(PetEgg, 2, 0);
        r.Give(Filler, 1, 1);
        r.Incubators.Seed(CollRig.Self,
            Slot(0, Incubator, 2),                                                       // empty, ready
            Slot(1, Incubator, 1, egg: PetEgg, start: DateTime.UtcNow, end: DateTime.UtcNow.AddHours(1)));   // already holds an egg

        var current = new[] { (Incubator, 0u, (byte)0, (byte)2), (Incubator, PetEgg, (byte)1, (byte)1) };
        Assert.Equal(current, List(await r.CallAsync(r.Handlers.Pet_1631, EggIn(CellOf(0), PetEgg, Incubator, 1))));          // slot already holds an egg
        Assert.Equal(current, List(await r.CallAsync(r.Handlers.Pet_1631, EggIn(CellOf(0), PetEgg, IncubatorBig, 0))));       // another incubator in that slot
        Assert.Equal(current, List(await r.CallAsync(r.Handlers.Pet_1631, EggIn(CellOf(0), PetEgg, 99999, 0))));              // not an incubator row
        Assert.Equal(current, List(await r.CallAsync(r.Handlers.Pet_1631, EggIn(CellOf(0), 123456, Incubator, 0))));          // not a pet's egg
        Assert.Equal(current, List(await r.CallAsync(r.Handlers.Pet_1631, EggIn(CellOf(1), PetEgg, Incubator, 0))));          // the cell holds another item
        Assert.Equal(current, List(await r.CallAsync(r.Handlers.Pet_1631, EggIn(CellOf(0), PetEgg, Incubator, 5))));          // no such slot
        Assert.Equal(current, List(await r.CallAsync(r.Handlers.Pet_1631, EggIn(CellOf(0), 0, Incubator, 0))));               // egg 0 never matches a pet
        Assert.Equal(2, r.CountOf(PetEgg));
        Assert.Equal(0u, r.Incubators.Stored(CollRig.Self).Single(s => s.Slot == 0).EggId);
    }

    [Fact]
    public async Task A_short_1631_body_is_not_answered()
    {
        await using var r = await CollRig.StartAsync();
        Assert.Empty(await r.CallAsync(r.Handlers.Pet_1631, new byte[11]));
    }

    // ------------------------------------------------------------------------------------------------ 1632 collect

    [Fact]
    public async Task Collecting_a_hatched_egg_adds_the_hatched_egg_item_takes_a_charge_empties_the_slot_and_notifies_in_order()
    {
        await using var r = await CollRig.StartAsync();
        r.Incubators.Seed(CollRig.Self, Slot(0, Incubator, 2, egg: PetEgg, start: DateTime.UtcNow.AddHours(-2), end: DateTime.UtcNow.AddHours(-1)));
        var before = DateTime.UtcNow;

        var frames = await r.CallAsync(r.Handlers.Pet_1632, Collect(Incubator, 0));

        var landed = r.Cellrow(0)!;
        Assert.Equal((PetHatched, 1), (landed.ItemId, landed.Count));                       // the HATCHED egg item, in the lowest free cell
        Assert.Equal(new ushort[] { ZoneS2COpcodes.OnPickupItem, Op2926 }, frames.Select(f => f.Op).ToArray());
        Assert.Equal(Hex(LootNotifyPackets.BuildPickupItem2333(ItemPackets.BuildItemRecord126(landed))), Hex(frames[0].Body));
        Assert.Equal(new[] { (Incubator, 0u, (byte)0, (byte)1) }, List(frames, 1));          // egg gone, one charge left
        var s = Assert.Single(r.Incubators.Stored(CollRig.Self));
        Assert.InRange(s.StartUtc, before.AddSeconds(-1), DateTime.UtcNow.AddSeconds(1));
        Assert.Equal(s.StartUtc, s.EndUtc);
        Assert.Equal(new[] { (PetHatched, 1) }, r.Quest.Gained.ToArray());                    // the quest hook fires once for the hatched egg
    }

    [Fact]
    public async Task Collecting_the_last_charge_removes_the_slot_and_the_hatched_egg_stacks_onto_a_held_stack()
    {
        await using var r = await CollRig.StartAsync();
        r.Give(PetHatched, 4, 3);
        r.Incubators.Seed(CollRig.Self,
            Slot(0, Incubator, 1, egg: PetEgg, start: DateTime.UtcNow.AddHours(-2), end: DateTime.UtcNow.AddMinutes(-1)),
            Slot(1, IncubatorBig, 1));

        var frames = await r.CallAsync(r.Handlers.Pet_1632, Collect(Incubator, 0));

        Assert.Equal(5, r.Cellrow(3)!.Count);                                                  // merged, not a new stack
        Assert.Null(r.Cellrow(0));
        Assert.Equal(new[] { (IncubatorBig, 0u, (byte)1, (byte)1) }, List(frames, 1));        // slot 0 (1 charge) was removed
    }

    [Fact]
    public async Task Every_1632_refusal_answers_one_2926_with_the_current_list_and_changes_nothing()
    {
        await using var r = await CollRig.StartAsync();
        r.Incubators.Seed(CollRig.Self,
            Slot(0, Incubator, 2, egg: PetEgg, start: DateTime.UtcNow, end: DateTime.UtcNow.AddHours(1)),       // not hatched yet
            Slot(1, IncubatorBig, 1));                                                                            // no egg

        var current = new[] { (Incubator, PetEgg, (byte)0, (byte)2), (IncubatorBig, 0u, (byte)1, (byte)1) };
        Assert.Equal(current, List(await r.CallAsync(r.Handlers.Pet_1632, Collect(Incubator, 0))));              // still incubating
        Assert.Equal(current, List(await r.CallAsync(r.Handlers.Pet_1632, Collect(IncubatorBig, 1))));           // nothing in it
        Assert.Equal(current, List(await r.CallAsync(r.Handlers.Pet_1632, Collect(IncubatorBig, 0))));           // wrong incubator for the slot
        Assert.Equal(current, List(await r.CallAsync(r.Handlers.Pet_1632, Collect(Incubator, 5))));              // no such slot
        Assert.Empty(r.Fx.Inventory.Rows);
        Assert.Empty(r.Quest.Gained);
        Assert.Empty(await r.CallAsync(r.Handlers.Pet_1632, new byte[4]));                                       // a short body is not answered
    }

    [Fact]
    public async Task A_hatched_egg_with_no_pet_row_or_with_a_full_bag_is_refused_without_taking_the_charge()
    {
        await using var r = await CollRig.StartAsync();
        var past = DateTime.UtcNow.AddHours(-1);
        r.Incubators.Seed(CollRig.Self, Slot(0, Incubator, 2, egg: 123456, start: past.AddHours(-1), end: past));   // an egg no PetInfo row names
        var orphan = new[] { (Incubator, 123456u, (byte)0, (byte)2) };
        Assert.Equal(orphan, List(await r.CallAsync(r.Handlers.Pet_1632, Collect(Incubator, 0))));
        Assert.Equal(123456u, r.Incubators.Stored(CollRig.Self).Single().EggId);

        r.Incubators.Seed(CollRig.Self, Slot(0, Incubator, 2, egg: PetEgg, start: past.AddHours(-1), end: past));
        for (var flat = 0; flat < 20; flat++) r.Give(Filler, 1, flat);                                          // a full main bag
        var full = new[] { (Incubator, PetEgg, (byte)0, (byte)2) };
        Assert.Equal(full, List(await r.CallAsync(r.Handlers.Pet_1632, Collect(Incubator, 0))));
        Assert.Equal(2, r.Incubators.Stored(CollRig.Self).Single().Charges);                                     // the charge was not taken
        Assert.Equal(PetEgg, r.Incubators.Stored(CollRig.Self).Single().EggId);
        Assert.Empty(r.Quest.Gained);
    }

    [Fact]
    public async Task An_unreadable_incubator_store_answers_a_collect_with_nothing()
    {
        await using var r = await CollRig.StartAsync();
        r.Incubators.ThrowOnGet = true;
        Assert.Empty(await r.CallAsync(r.Handlers.Pet_1632, Collect(Incubator, 0)));
        Assert.Empty(await r.CallAsync(r.Handlers.Pet_1631, EggIn(CellOf(0), PetEgg, Incubator, 0)));
    }
}
