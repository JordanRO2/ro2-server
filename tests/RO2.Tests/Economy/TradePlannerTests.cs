using RO2.Economy.Domain;
using Xunit;

namespace RO2.Tests.Economy;

/// <summary>
/// Pins <see cref="TradePlanner"/> as the confirm handler's PlanPlacement planned it: stacks merge into the
/// recipient's held stacks first (cell order, up to Stack_Max), the rest takes free cells, a recipient's own offered
/// uids are never merge targets, and a recipient with too few cells refuses the whole direction.
/// </summary>
public sealed class TradePlannerTests
{
    private const uint Stackable = 5102;
    private const uint Single = 5101;

    private static int StackMax(uint id) => id == Stackable ? 120 : 1;

    [Fact]
    public void MergesIntoTheHeldStack_ThenTakesACellForTheRemainder()
    {
        var offered = new[] { new TradePlanItem(1, Stackable, 100, 100) };
        var held = new[] { new BagStack(9, Stackable, 50, 3) };

        var plan = TradePlanner.PlanPlacement(offered, held, [0, 1], new HashSet<ulong>(), StackMax)!;

        Assert.Equal(120, plan.HeldCounts[9]);           // topped up to Stack_Max
        var move = Assert.Single(plan.Moves);
        Assert.Equal((short)0, move.Cell);               // the remaining 30 take the first free cell
        Assert.Equal(30, move.CellCount);
        Assert.True(move.WholeStack);
    }

    [Fact]
    public void AStackTheRecipientIsHandingOver_IsNeverAMergeTarget()
    {
        var offered = new[] { new TradePlanItem(1, Stackable, 10, 10) };
        var held = new[] { new BagStack(9, Stackable, 50, 3) };

        var plan = TradePlanner.PlanPlacement(offered, held, [0], new HashSet<ulong> { 9 }, StackMax)!;

        Assert.Empty(plan.HeldCounts);
        Assert.Equal((short)0, plan.Moves[0].Cell);
    }

    [Fact]
    public void ALaterOfferOfTheSameTemplate_FillsTheCellPlannedForTheEarlierOne()
    {
        var offered = new[] { new TradePlanItem(1, Stackable, 100, 100), new TradePlanItem(2, Stackable, 15, 15) };

        var plan = TradePlanner.PlanPlacement(offered, [], [4, 5], new HashSet<ulong>(), StackMax)!;

        Assert.Equal((short)4, plan.Moves[0].Cell);
        Assert.Equal(115, plan.Moves[0].CellCount);      // the second offer merged into the first one's cell
        Assert.Equal((short)-1, plan.Moves[1].Cell);
    }

    [Fact]
    public void ANeededCellWithNoneLeft_RefusesTheDirection()
    {
        var offered = new[] { new TradePlanItem(1, Single, 1, 1), new TradePlanItem(2, Single, 1, 1) };

        Assert.Null(TradePlanner.PlanPlacement(offered, [], [0], new HashSet<ulong>(), StackMax));
    }

    [Fact]
    public void APartialOffer_IsNotAWholeStack()
    {
        var offered = new[] { new TradePlanItem(1, Stackable, 10, 4) };

        var move = TradePlanner.PlanPlacement(offered, [], [7], new HashSet<ulong>(), StackMax)!.Moves[0];

        Assert.False(move.WholeStack);
        Assert.Equal(4, move.CellCount);
    }

    [Fact]
    public void PlanSwap_ExcludesEachSidesOwnOfferedUids_AndReportsTheFullSide()
    {
        var a = new TradePlanSide([new TradePlanItem(1, Stackable, 10, 10)], [new BagStack(1, Stackable, 10, 0)], [1]);
        var b = new TradePlanSide([new TradePlanItem(2, Stackable, 5, 5)], [new BagStack(2, Stackable, 5, 0)], []);

        var plan = TradePlanner.PlanSwap(a, b, StackMax);

        // A -> B: B's only stack of the template is its own offered uid 2, so it is not a merge target and B has no cell.
        Assert.Null(plan.AToB);
        // B -> A: A's stack is its own offered uid 1, so the 5 take A's free cell 1.
        Assert.Equal((short)1, plan.BToA!.Moves[0].Cell);
    }
}
