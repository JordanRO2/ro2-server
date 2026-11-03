using RO2.Collections.Domain;
using Xunit;
using static RO2.Tests.CollectionCharacterization.CollData;

namespace RO2.Tests.CollectionCharacterization;

/// <summary>
/// B10 S6: the pure Decide helpers of RO2.Collection.Domain called directly, with no store, repository or host. The handler and service paths
/// that use them are pinned by the S1 characterization tests.
/// </summary>
public class CollectionDecisionTests
{
    // ------------------------------------------------------------------------------------------------ MonsterFeedDecision (Mon: Exp 2,3,1,1 over levels 1..4)

    [Fact]
    public void A_dna_for_an_unregistered_collection_registers_it_at_level_1_whatever_is_stored()
    {
        Assert.Equal(new MonsterFeedOutcome(MonsterFeedKind.Registered, 1, 0, 30008),
            MonsterFeedDecision.Decide(MonsterRows(), Mon, registered: false, level: 3, exp: 7));
    }

    [Theory]
    [InlineData(1, 0u, 1, 1u)]   // exp 1 of 2: banked
    [InlineData(1, 1u, 2, 0u)]   // exp reaches 2: level up, exp resets
    [InlineData(2, 2u, 3, 0u)]   // exp reaches 3
    [InlineData(3, 0u, 4, 0u)]   // exp reaches 1
    public void A_dna_for_a_registered_collection_adds_one_exp_and_levels_up_at_the_row_exp(int level, uint exp, int expectedLevel, uint expectedExp)
    {
        Assert.Equal(new MonsterFeedOutcome(MonsterFeedKind.ExpGained, (byte)expectedLevel, expectedExp, 30009),
            MonsterFeedDecision.Decide(MonsterRows(), Mon, registered: true, (byte)level, exp));
    }

    [Fact]
    public void A_dna_at_the_highest_level_is_refused_with_the_stored_values_unchanged()
    {
        Assert.Equal(new MonsterFeedOutcome(MonsterFeedKind.MaxLevel, 4, 0, 30005),
            MonsterFeedDecision.Decide(MonsterRows(), Mon, registered: true, 4, 0));
        Assert.Equal(new MonsterFeedOutcome(MonsterFeedKind.MaxLevel, 1, 3, 30005),
            MonsterFeedDecision.Decide(MonsterRows(), MonSingle, registered: true, 1, 3));
    }

    // ------------------------------------------------------------------------------------------------ PetEggDecision (Pet cap 25)

    [Fact]
    public void An_egg_for_a_pet_not_in_the_book_registers_it_at_loyalty_0()
    {
        Assert.Equal(new PetEggOutcome(PetEggKind.Registered, 0, 1, 0), PetEggDecision.Decide(PetRows()[0], registered: false, loyalty: 0));
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(10, 20)]
    [InlineData(20, 25)]   // min(loyalty + 10, cap)
    public void An_egg_for_a_booked_pet_below_the_cap_feeds_it(int loyalty, int expected)
    {
        Assert.Equal(new PetEggOutcome(PetEggKind.Fed, (ushort)expected, 2, 0), PetEggDecision.Decide(PetRows()[0], registered: true, (ushort)loyalty));
    }

    [Fact]
    public void An_egg_for_a_pet_at_the_cap_is_refused_with_30001_and_the_loyalty_unchanged()
    {
        Assert.Equal(new PetEggOutcome(PetEggKind.Capped, 25, 0, 30001), PetEggDecision.Decide(PetRows()[0], registered: true, 25));
    }

    // ------------------------------------------------------------------------------------------------ ActivePetRule

    [Fact]
    public void Set_active_pet_accepts_a_booked_pet_with_the_flag_from_its_loyalty_and_clears_on_0()
    {
        var book = new CollectionDocument { Id = 1, RegisteredPetIds = { Pet }, ActivePetId = 0 };
        book.SetLoyalty(Pet, PetCap);
        Assert.Equal(new ActivePetOutcome(true, Pet, 1), ActivePetRule.Decide(PetRows(), book, Pet));       // loyalty at LoyaltyLevel_1: level 1
        book.SetLoyalty(Pet, 5);
        Assert.Equal(new ActivePetOutcome(true, Pet, 0), ActivePetRule.Decide(PetRows(), book, Pet));
        Assert.Equal(new ActivePetOutcome(true, 0, 0), ActivePetRule.Decide(PetRows(), book, 0));
    }

    [Fact]
    public void Set_active_pet_refuses_an_unbooked_pet_and_echoes_the_current_pair_unchanged()
    {
        var book = new CollectionDocument { Id = 1, RegisteredPetIds = { Pet }, ActivePetId = Pet, ActivePetFlag = 1 };
        Assert.Equal(new ActivePetOutcome(false, Pet, 1), ActivePetRule.Decide(PetRows(), book, PetBig));
    }

    // ------------------------------------------------------------------------------------------------ PlanCombine

    [Fact]
    public void Combine_piece_needs_scale_by_times_group_repeated_ids_and_skip_empty_pieces_for_both_recipes()
    {
        Assert.Equal([(PieceA, 9), (PieceB, 3)], PlanCombine.PieceNeeds(MonsterRows()[0], 3));          // Piece1 x2 + Piece4 x1 of A, Piece2 x1 of B
        Assert.Equal([(PetPieceA, 6), (PetPieceB, 3)], PlanCombine.PieceNeeds(PetRows()[0], 3));
        Assert.Empty(PlanCombine.PieceNeeds(PetRows()[1], 5));                                          // no pieces
        Assert.Empty(PlanCombine.PieceNeeds(PetRows()[0], 0));                                          // times 0
    }

    [Theory]
    [InlineData(3, 2, 6)]
    [InlineData(3, 0, 3)]   // a row count of 0 still takes 1 per combine
    public void The_consumable_need_is_the_row_count_but_at_least_one_per_combine(int times, int perCombine, int expected)
    {
        Assert.Equal(expected, PlanCombine.ConsumableNeed(times, (byte)perCombine));
    }
}
