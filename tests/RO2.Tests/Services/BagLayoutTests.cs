using RO2.Inventory.Domain;
using Xunit;

namespace RO2.Tests.Services;

/// <summary>
/// <see cref="BagLayout"/> and <see cref="BagStackPlanner.FindMergeTarget"/> straight over primitives, with the same
/// vectors <see cref="BagSlotsTests"/> recorded through the host and provider entry points before the move.
/// </summary>
public class BagLayoutTests
{
    private static short[] Cells(IEnumerable<int> flats) => flats.Select(f => (short)f).ToArray();

    [Fact]
    public void FreeCells_MainBagGaps_LowestFirst()
        => Assert.Equal(new short[] { 2, 3, 7, 8, 9, 10, 11, 13, 14, 15, 16, 17, 18 },
            BagLayout.FreeCells([0, 1, 4, 5, 6, 12, 19], []));

    [Fact]
    public void FreeCells_FullMainBag_IsEmpty()
        => Assert.Empty(BagLayout.FreeCells(Enumerable.Range(0, 20), []));

    [Fact]
    public void FreeCells_ExtraBagAtItsStride_FollowsMainBag()
        => Assert.Equal(Cells(Enumerable.Range(55, 30).Where(f => f != 56 && f != 60)),
            BagLayout.FreeCells(Enumerable.Range(0, 20).Concat([56, 60]), [new ExtraBag(1, 30)]));

    [Fact]
    public void FreeCells_WalksBagsByCell_ClampsSize_AndIgnoresUnusableBags()
    {
        Assert.Equal(Cells(Enumerable.Range(20, 30).Concat(Enumerable.Range(90, 10))),
            BagLayout.FreeCells(Enumerable.Range(0, 20), [new ExtraBag(2, 10), new ExtraBag(0, 30)]));
        Assert.Equal(Cells(Enumerable.Range(20, 35)),
            BagLayout.FreeCells(Enumerable.Range(0, 20), [new ExtraBag(0, 99)]));
        Assert.Empty(BagLayout.FreeCells(Enumerable.Range(0, 20),
            [new ExtraBag(0, -5), new ExtraBag(3, 30), new ExtraBag(-1, 30)]));
    }

    [Fact]
    public void FirstFreeCell_IsTheFirstOfFreeCells_OrMinusOneWhenFull()
    {
        Assert.Equal((short)2, BagLayout.FirstFreeCell([0, 1, 4], []));
        Assert.Equal((short)20, BagLayout.FirstFreeCell(Enumerable.Range(0, 20), [new ExtraBag(0, 5)]));
        Assert.Equal((short)-1, BagLayout.FirstFreeCell(Enumerable.Range(0, 20), []));
    }

    [Fact]
    public void ChooseCell_PrefersTheRequestedCellOnlyWhenFreeAndUsable()
    {
        ExtraBag[] bags = [new ExtraBag(0, 5)];
        Assert.Equal((short)7, BagLayout.ChooseCell([0, 1], bags, 7));        // free main-bag cell
        Assert.Equal((short)2, BagLayout.ChooseCell([0, 1, 7], bags, 7));     // taken -> lowest free
        Assert.Equal((short)24, BagLayout.ChooseCell([], bags, 24));          // last cell of the 5-cell bag
        Assert.Equal((short)0, BagLayout.ChooseCell([], bags, 25));           // beyond its size -> lowest free
        Assert.Equal((short)0, BagLayout.ChooseCell([], bags, 55));           // bag cell 1 not equipped
        Assert.Equal((short)0, BagLayout.ChooseCell([], bags, 125));          // bag cell 3 does not exist
        Assert.Equal((short)0, BagLayout.ChooseCell([], bags, -1));
        Assert.Equal((short)20, BagLayout.ChooseCell(Enumerable.Range(0, 20), bags, 3));
        Assert.Equal((short)-1, BagLayout.ChooseCell(Enumerable.Range(0, 25), bags, 3));
    }

    [Theory]
    [InlineData(new int[0], 0)]
    [InlineData(new[] { 0, 1, 2 }, 3)]
    [InlineData(new[] { 0, 2, 3 }, 1)]
    [InlineData(new[] { -1, 1 }, 0)]
    [InlineData(new[] { -1, 0, 1, 5 }, 2)]
    [InlineData(new[] { 1, 1, 0, 0, 2 }, 3)]
    public void LowestFreeSlot_PicksTheFirstUnoccupiedIndex(int[] occupied, int expected)
        => Assert.Equal((short)expected, BagLayout.LowestFreeSlot(occupied));

    [Fact]
    public void PlanSlotRepair_FirstClaimantKeeps_DuplicatesAndUnplacedTakeTheNextFreeIndex()
    {
        var moves = BagLayout.PlanSlotRepair([(1UL, (short)0), (2, 2), (3, 2), (4, 2), (5, -1)]);
        Assert.Equal([new SlotAssignment(3, 1), new SlotAssignment(4, 3), new SlotAssignment(5, 4)], moves);
    }

    [Fact]
    public void PlanSlotRepair_AValidBag_MovesNothing_AndIsOrderIndependent()
    {
        Assert.Empty(BagLayout.PlanSlotRepair([(9UL, (short)3), (1, 0), (5, 1)]));
        // Unplaced rows take the lowest hole in uid order regardless of input order.
        var moves = BagLayout.PlanSlotRepair([(8UL, (short)-1), (6, 3), (7, -1), (5, 0)]);
        Assert.Equal([new SlotAssignment(7, 1), new SlotAssignment(8, 2)], moves);
    }

    [Fact]
    public void FindMergeTarget_LowestCellThatFitsWhole_EarlierRowOnATie_MinusOneWhenNone()
    {
        BagStack[] owned = [new(1, 77, 9, 8), new(2, 77, 5, 3), new(3, 77, 10, 0), new(4, 77, 5, 3), new(5, 88, 1, 1)];
        Assert.Equal(1, BagStackPlanner.FindMergeTarget(owned, 77, 3, 10));    // cell 3, earlier of the tie
        Assert.Equal(-1, BagStackPlanner.FindMergeTarget(owned, 77, 7, 10));   // nothing fits whole
        Assert.Equal(-1, BagStackPlanner.FindMergeTarget(owned, 77, 1, 1));    // not stackable
        Assert.Equal(-1, BagStackPlanner.FindMergeTarget(owned, 99, 1, 10));   // item not held
        Assert.Equal(1, BagStackPlanner.StackSize(0));
        Assert.Equal(99, BagStackPlanner.StackSize(99));
    }
}
