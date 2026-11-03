using RO2.Collections.Domain;
using RO2.GameData;
using RO2.GameData.Tables;
using RO2.Network.Protocol;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Services;
using Xunit;
using static RO2.Tests.CollectionCharacterization.CollData;

namespace RO2.Tests.CollectionCharacterization;

/// <summary>
/// B10 S1: the PURE Collections decisions as they stand today, over the synthetic GameData of <see cref="CollData"/>: the collect-rate
/// tiers, the pet loyalty arithmetic and cap, the active-pet flag derivation, the record projections (monster/pet state) and the
/// incubator slot rules. They are what a later slice MOVES (MonsterCollectionRules, PetCollectionBook, PetIncubatorRules,
/// CollectionDocument members), so a byte-identical move keeps them green. Expected values were observed from the code, quirks included.
/// </summary>
public class CollectionRulesCharacterizationTests
{
    private static readonly DateTime T0 = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    // ------------------------------------------------------------------------------------------------ result codes and constants

    [Fact]
    public void The_result_codes_and_the_feed_constant_are_pinned()
    {
        Assert.Equal((ushort)30005, MonsterCollectionRules.ResultMaxLevel);
        Assert.Equal((ushort)30008, MonsterCollectionRules.ResultRegistered);
        Assert.Equal((ushort)30009, MonsterCollectionRules.ResultExpGained);
        Assert.Equal((ushort)30004, MCollectionUseProteinHandler.ResultNoDnaInCell);
        Assert.Equal((ushort)30001, PCollectionUsePetEggHandler.ResultLoyaltyCapped);
        Assert.Equal((ushort)30004, PCollectionUsePetEggHandler.ResultNoEgg);
        Assert.Equal((ushort)10, PetCollectionBook.LoyaltyPerFeed);
        Assert.Equal(2, PetIncubatorRules.SlotCount);
        Assert.Equal(7, PCollectionCombinEggBlankHandler.RequestLength);
    }

    // ------------------------------------------------------------------------------------------------ collect-rate tiers (RateFor)

    [Theory]
    [InlineData(0, 1, 3, 1000u)]
    [InlineData(2, 1, 3, 1000u)]       // End is inclusive
    [InlineData(3, 2, 4, 2000u)]       // Start is inclusive
    [InlineData(5, 2, 4, 2000u)]
    [InlineData(6, 3, 5, 3000u)]
    [InlineData(9, 3, 5, 3000u)]
    [InlineData(10, 3, 5, 3000u)]      // above every range: the row with the highest Num_End
    [InlineData(1000, 3, 5, 3000u)]
    [InlineData(-1, 3, 5, 3000u)]      // QUIRK: below every range is also the highest tier, not the lowest
    public async Task RateFor_picks_the_row_whose_range_holds_the_count_else_the_highest_tier(int count, byte index, byte skillMax, uint cooldown)
    {
        var store = await StoreAsync();
        Assert.Equal(new CollectRateHeader(index, skillMax, cooldown), MonsterCollectionRules.RateFor(store, count));
    }

    [Theory]
    [InlineData(3, 2)]                 // between [0,2] and [5,9]: neither holds it, the highest Num_End seen wins
    [InlineData(4, 2)]
    [InlineData(2, 1)]
    [InlineData(5, 2)]
    public async Task RateFor_over_a_table_with_a_gap_falls_to_the_highest_Num_End(int count, int expectedIndex)
    {
        var store = await StoreAsync([
            new CollectRate { Index = 1, Num_Start = 0, Num_End = 2, Skill_Max = 3, Skill_Cooltime = 1000 },
            new CollectRate { Index = 2, Num_Start = 5, Num_End = 9, Skill_Max = 4, Skill_Cooltime = 2000 }]);
        Assert.Equal(expectedIndex, MonsterCollectionRules.RateFor(store, count).Index);
    }

    [Fact]
    public async Task RateFor_with_no_CollectRate_rows_is_the_zero_header()
    {
        var store = await StoreAsync([]);
        Assert.Equal(default, MonsterCollectionRules.RateFor(store, 3));
    }

    // ------------------------------------------------------------------------------------------------ pet loyalty arithmetic

    [Theory]
    [InlineData(25, 0, 10)]
    [InlineData(25, 10, 20)]
    [InlineData(25, 15, 25)]           // +10 is clamped to the cap
    [InlineData(25, 20, 25)]
    [InlineData(25, 24, 25)]
    [InlineData(25, 25, null)]         // AT the cap: nothing to feed
    [InlineData(25, 26, null)]         // above it: still nothing
    [InlineData(25, 65535, null)]
    [InlineData(0, 0, null)]           // a pet whose cap is 0 can never be fed
    [InlineData(5, 0, 5)]
    [InlineData(65535, 65530, 65535)]  // int arithmetic: 65540 is clamped, never wrapped
    [InlineData(65535, 65535, null)]
    public void FedLoyalty_adds_ten_clamped_to_the_cap_and_is_null_at_or_above_it(int cap, int loyalty, int? expected)
    {
        var pet = new PetInfo { NPC_ID = 1, LoyaltyLevel_1 = (ushort)cap };
        Assert.Equal(expected is null ? null : (ushort)expected, PetCollectionBook.FedLoyalty(pet, (ushort)loyalty));
    }

    [Fact]
    public void LoyaltyOf_clamps_to_ushort_the_first_entry_of_a_duplicate_wins_and_SetLoyalty_updates_that_first_entry()
    {
        var d = new CollectionDocument
        {
            Id = 1,
            PetLoyalties =
            {
                new PetLoyalty { PetId = 1, Loyalty = -5 }, new PetLoyalty { PetId = 2, Loyalty = 70_000 },
                new PetLoyalty { PetId = 3, Loyalty = 7 }, new PetLoyalty { PetId = 3, Loyalty = 99 },
            },
        };
        Assert.Equal((ushort)0, d.LoyaltyOf(1));            // negative -> 0
        Assert.Equal((ushort)65535, d.LoyaltyOf(2));        // above ushort -> 65535
        Assert.Equal((ushort)7, d.LoyaltyOf(3));            // the first of two entries for the pet
        Assert.Equal((ushort)0, d.LoyaltyOf(404));          // never fed / not booked

        d.SetLoyalty(3, 40);
        Assert.Equal(2, d.PetLoyalties.Count(p => p.PetId == 3));
        Assert.Equal(40, d.PetLoyalties.First(p => p.PetId == 3).Loyalty);   // the first entry changed ...
        Assert.Equal(99, d.PetLoyalties.Last(p => p.PetId == 3).Loyalty);    // ... the duplicate is left alone
        d.SetLoyalty(77, 10);
        Assert.Equal(5, d.PetLoyalties.Count);               // a new pet appends one entry at the end
        Assert.Equal(77u, d.PetLoyalties[^1].PetId);
        d.SetLoyalty(77, 20);
        Assert.Equal(5, d.PetLoyalties.Count);               // an existing pet never appends
    }

    // ------------------------------------------------------------------------------------------------ pet projections

    [Theory]
    [InlineData(0, PetEffect0, 0)]
    [InlineData(24, PetEffect0, 0)]
    [InlineData(25, PetEffect1, 1)]    // >= LoyaltyLevel_1, inclusive
    [InlineData(26, PetEffect1, 1)]
    public async Task A_known_pet_state_carries_the_effect_and_level_of_the_highest_loyalty_level_reached(int loyalty, uint effect, byte level)
    {
        var store = await StoreAsync();
        var state = PetCollectionBook.State(store, Pet, (ushort)loyalty);
        Assert.Equal(new PetCollectionState(Pet, effect, level, (ushort)loyalty), state);
        Assert.Equal(level, PetCollectionBook.ActiveFlag(store, Pet, (ushort)loyalty));
    }

    [Fact]
    public async Task An_unknown_pet_or_a_null_store_keeps_only_the_id_and_the_loyalty_is_dropped()
    {
        var store = await StoreAsync();
        Assert.Equal(new PetCollectionState(PetUnknown, 0, 0, 0), PetCollectionBook.State(store, PetUnknown, 300));   // loyalty NOT echoed
        Assert.Equal(new PetCollectionState(Pet, 0, 0, 0), PetCollectionBook.State(null, Pet, 300));
        Assert.Equal((byte)0, PetCollectionBook.ActiveFlag(store, PetUnknown, 300));
        Assert.Equal((byte)0, PetCollectionBook.ActiveFlag(null, Pet, 300));
        Assert.Equal((byte)0, PetCollectionBook.ActiveFlag(store, 0, 300));    // no active pet
    }

    [Fact]
    public async Task The_pet_book_is_distinct_ascending_skips_zero_and_each_pet_is_at_its_stored_loyalty()
    {
        var store = await StoreAsync();
        var book = new CollectionDocument { Id = 1, RegisteredPetIds = { PetBig, 0, Pet, PetBig, PetUnknown } };
        book.SetLoyalty(Pet, 25);
        book.SetLoyalty(PetUnknown, 9);

        var states = PetCollectionBook.BookStates(store, book);
        Assert.Equal(new[]
        {
            new PetCollectionState(Pet, PetEffect1, 1, 25),
            new PetCollectionState(PetBig, 11, 0, 0),
            new PetCollectionState(PetUnknown, 0, 0, 0),
        }, states);
    }

    // ------------------------------------------------------------------------------------------------ monster projections

    [Fact]
    public async Task MaxLevel_Row_and_the_lookups_read_the_CollectMonster_table_by_npc_and_level()
    {
        var store = await StoreAsync();
        Assert.Equal((byte)4, MonsterCollectionRules.MaxLevel(store, Mon));
        Assert.Equal((byte)1, MonsterCollectionRules.MaxLevel(store, MonSingle));
        Assert.Equal((byte)0, MonsterCollectionRules.MaxLevel(store, 99999));
        Assert.Equal(Skill3, MonsterCollectionRules.Row(store, Mon, 3)!.SkillID);
        Assert.Null(MonsterCollectionRules.Row(store, Mon, 5));
        Assert.Null(MonsterCollectionRules.Row(store, Mon, 0));
        Assert.Equal(Pet, MonsterCollectionRules.Pet(store, Pet)!.NPC_ID);
        Assert.Null(MonsterCollectionRules.Pet(store, PetUnknown));
    }

    [Fact]
    public async Task Monster_State_an_unregistered_collection_is_level_0_with_the_level_1_skill_a_registered_one_defaults_to_level_1_exp_0()
    {
        var store = await StoreAsync();
        var progress = new MonsterCollectionProgressDocument
        {
            Id = 1,
            Entries = { new MonsterCollectionEntry { NpcId = Mon, Level = 3, Exp = 1 }, new MonsterCollectionEntry { NpcId = MonSingle, Level = 9, Exp = 4 } },
        };

        Assert.Equal(new MonsterCollectionState(Mon, Skill1, 0, 0), MonsterCollectionRules.State(store, progress, Mon, registered: false));   // stored progress is ignored
        Assert.Equal(new MonsterCollectionState(Mon, Skill3, 3, 1), MonsterCollectionRules.State(store, progress, Mon, registered: true));
        Assert.Equal(new MonsterCollectionState(MonNoRecipe, 5301, 1, 0), MonsterCollectionRules.State(store, progress, MonNoRecipe, registered: true));   // no entry: level 1 / exp 0
        Assert.Equal(new MonsterCollectionState(MonSingle, 0, 9, 4), MonsterCollectionRules.State(store, progress, MonSingle, registered: true));          // a level with no row: skill 0
        Assert.Equal(new MonsterCollectionState(0, 0, 0, 0), MonsterCollectionRules.State(store, progress, 0, registered: false));
        Assert.Equal(new MonsterCollectionState(Mon, Skill2, 2, 7), MonsterCollectionRules.State(store, Mon, 2, 7));   // the raw form derives the skill from the level
    }

    [Fact]
    public async Task The_monster_book_is_distinct_ascending_and_skips_zero()
    {
        var store = await StoreAsync();
        var progress = new MonsterCollectionProgressDocument { Id = 1, Entries = { new MonsterCollectionEntry { NpcId = Mon, Level = 2, Exp = 1 } } };

        var states = MonsterCollectionRules.BookStates(store, [MonSingle, 0, Mon, MonSingle], progress);
        Assert.Equal(new[] { new MonsterCollectionState(Mon, Skill2, 2, 1), new MonsterCollectionState(MonSingle, 5201, 1, 0) }, states);
        Assert.Empty(MonsterCollectionRules.BookStates(store, [0, 0], progress));
    }

    // ------------------------------------------------------------------------------------------------ incubator slot rules

    [Fact]
    public void TryPlace_refuses_a_slot_index_of_two_or_more_an_occupied_slot_and_zero_charges_and_stores_the_empty_form_otherwise()
    {
        var slots = new List<PetIncubatorSlot>();
        Assert.False(PetIncubatorRules.TryPlace(slots, 2, Incubator, 2, T0));
        Assert.False(PetIncubatorRules.TryPlace(slots, 255, Incubator, 2, T0));
        Assert.False(PetIncubatorRules.TryPlace(slots, 0, Incubator, 0, T0));
        Assert.Empty(slots);

        Assert.True(PetIncubatorRules.TryPlace(slots, 1, Incubator, 2, T0));
        var s = Assert.Single(slots);
        Assert.Equal((byte)1, s.Slot);
        Assert.Equal((Incubator, (byte)2, 0u), (s.IncubatorId, s.Charges, s.EggId));
        Assert.Equal((T0, T0), (s.StartUtc, s.EndUtc));                       // an empty slot: Start == End == the placing instant
        Assert.False(PetIncubatorRules.TryPlace(slots, 1, IncubatorBig, 1, T0));
        Assert.Equal(Incubator, slots[0].IncubatorId);
    }

    [Fact]
    public void TryStartIncubation_needs_the_matching_incubator_a_charge_an_empty_slot_and_a_non_zero_egg()
    {
        var slots = new List<PetIncubatorSlot>();
        PetIncubatorRules.TryPlace(slots, 0, Incubator, 2, T0);

        Assert.False(PetIncubatorRules.TryStartIncubation(slots, 1, Incubator, PetEgg, T0, T0.AddHours(1)));      // no such slot
        Assert.False(PetIncubatorRules.TryStartIncubation(slots, 0, IncubatorBig, PetEgg, T0, T0.AddHours(1)));   // another incubator
        Assert.False(PetIncubatorRules.TryStartIncubation(slots, 0, Incubator, 0, T0, T0.AddHours(1)));           // egg 0
        slots[0].Charges = 0;
        Assert.False(PetIncubatorRules.TryStartIncubation(slots, 0, Incubator, PetEgg, T0, T0.AddHours(1)));      // no charge left
        slots[0].Charges = 2;
        Assert.True(PetIncubatorRules.TryStartIncubation(slots, 0, Incubator, PetEgg, T0, T0.AddHours(1)));
        Assert.Equal((PetEgg, T0, T0.AddHours(1), (byte)2), (slots[0].EggId, slots[0].StartUtc, slots[0].EndUtc, slots[0].Charges));
        Assert.False(PetIncubatorRules.TryStartIncubation(slots, 0, Incubator, PetEgg, T0, T0.AddHours(2)));       // already holds an egg
    }

    [Fact]
    public void CollectableSlot_is_inclusive_of_the_hatch_instant_and_ApplyCollect_takes_one_charge_and_removes_the_last()
    {
        var slots = new List<PetIncubatorSlot>();
        PetIncubatorRules.TryPlace(slots, 0, Incubator, 2, T0);
        PetIncubatorRules.TryStartIncubation(slots, 0, Incubator, PetEgg, T0, T0.AddHours(1));

        Assert.Null(PetIncubatorRules.CollectableSlot(slots, 0, Incubator, T0.AddHours(1).AddTicks(-1)));   // one tick early
        Assert.NotNull(PetIncubatorRules.CollectableSlot(slots, 0, Incubator, T0.AddHours(1)));              // exactly at EndUtc
        Assert.Null(PetIncubatorRules.CollectableSlot(slots, 0, IncubatorBig, T0.AddHours(2)));
        Assert.Null(PetIncubatorRules.CollectableSlot(slots, 1, Incubator, T0.AddHours(2)));

        var at = T0.AddHours(3);
        PetIncubatorRules.ApplyCollect(slots, slots[0], at);
        Assert.Equal((0u, (byte)1, at, at), (slots[0].EggId, slots[0].Charges, slots[0].StartUtc, slots[0].EndUtc));
        Assert.Null(PetIncubatorRules.CollectableSlot(slots, 0, Incubator, at));      // an emptied slot holds no egg

        PetIncubatorRules.TryStartIncubation(slots, 0, Incubator, PetEgg, at, at.AddMinutes(1));
        PetIncubatorRules.ApplyCollect(slots, slots[0], at.AddMinutes(1));
        Assert.Empty(slots);                                                          // the last charge removes the slot
    }

    [Fact]
    public void Copy_is_independent_and_RestoreSlot_removes_then_appends_so_the_slot_moves_to_the_end()
    {
        var slots = new List<PetIncubatorSlot>();
        PetIncubatorRules.TryPlace(slots, 0, Incubator, 2, T0);
        PetIncubatorRules.TryPlace(slots, 1, IncubatorBig, 1, T0);
        var before = PetIncubatorRules.Copy(slots[0]);
        slots[0].Charges = 0;
        Assert.Equal((byte)2, before.Charges);                                          // the copy did not follow

        PetIncubatorRules.RestoreSlot(slots, before);
        Assert.Equal(new byte[] { 1, 0 }, slots.Select(s => s.Slot).ToArray());         // slot 0 is now LAST
        Assert.Equal((byte)2, slots[1].Charges);

        PetIncubatorRules.RestoreSlot(slots, new PetIncubatorSlot { Slot = 1, IncubatorId = 5, Charges = 1 });   // replaces, not duplicates
        Assert.Equal(2, slots.Count);
        Assert.Equal(5u, slots.Single(s => s.Slot == 1).IncubatorId);

        var empty = new List<PetIncubatorSlot>();
        PetIncubatorRules.RestoreSlot(empty, before);                                    // an absent slot is simply added
        Assert.Single(empty);
    }

    [Fact]
    public async Task Hatch_seconds_are_speed_times_incubation_time_over_10000_in_integer_arithmetic_and_PetByEgg_never_matches_egg_0()
    {
        var store = await StoreAsync();
        var inc = PetIncubatorStore.Incubator(store, Incubator)!;
        var big = PetIncubatorStore.Incubator(store, IncubatorBig)!;
        var pet = PetIncubatorStore.PetByEgg(store, PetEgg)!;

        Assert.Equal(Pet, pet.NPC_ID);
        Assert.Equal(600u, PetIncubatorStore.IncubationSeconds(inc, pet));            // 10000 * 600 / 10000
        Assert.Equal(300u, PetIncubatorStore.IncubationSeconds(big, pet));            // 5000 * 600 / 10000
        Assert.Equal(300u, PetIncubatorStore.IncubationSeconds(big, pet with { Incubation_Time = 601 }));   // 300.5 truncates
        Assert.Null(PetIncubatorStore.Incubator(store, 99999));
        Assert.Null(PetIncubatorStore.PetByEgg(store, 0));                              // PetNoRecipe has Pet_Egg_ID 0 but egg 0 never matches
        Assert.Null(PetIncubatorStore.PetByEgg(store, 123456));
    }

    // ------------------------------------------------------------------------------------------------ 1626 piece needs

    [Fact]
    public async Task The_egg_combine_piece_needs_scale_by_times_group_repeated_items_and_skip_unused_pieces()
    {
        var store = await StoreAsync();
        var pet = MonsterCollectionRules.Pet(store, Pet)!;
        Assert.Equal(new[] { (PetPieceA, 6), (PetPieceB, 3) }, PCollectionCombinEggBlankHandler.PieceNeeds(pet, 3).ToArray());   // Piece1 x2, Piece2 x1
        Assert.Empty(PCollectionCombinEggBlankHandler.PieceNeeds(MonsterCollectionRules.Pet(store, PetBig)!, 5));                // no pieces
        var repeated = pet with { ItemID_Piece3 = PetPieceA, Num_Piece3 = 1, ItemID_Piece4 = PetPieceB, Num_Piece4 = 0 };
        Assert.Equal(new[] { (PetPieceA, 3), (PetPieceB, 1) }, PCollectionCombinEggBlankHandler.PieceNeeds(repeated, 1).ToArray());   // 2 + 1 grouped; a 0-count piece skipped
    }
}
