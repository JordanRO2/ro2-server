using System.Collections.Generic;
using RO2.Inventory.Domain;
using Xunit;

namespace RO2.Tests.Inventory;

/// <summary>
/// Pins <see cref="BagStackPlanner.PlanSequence"/>, the shared sequential add planner that the kill auto-loot and
/// the dungeon-finder reward both delegate to. Covers merge, overflow, the refused-consumes-no-cell rule, the
/// merge-into-a-stack-opened-earlier case, skipped (gold) entries, and a non-stackable item.
/// </summary>
public class PlanSequenceTests
{
    private const uint A = 1000, B = 2000;

    private static IReadOnlyList<SequencedPlacement?> Run(
        IReadOnlyList<BagStack> owned, short freeCount, params (uint, int, byte)[] entries)
    {
        var free = new List<short>();
        for (short s = 0; s < freeCount; s++) free.Add(s);
        return BagStackPlanner.PlanSequence(owned, free, entries);
    }

    [Fact]
    public void MergesWholeQuantityIntoLowestExistingStack()
    {
        var owned = new[] { new BagStack(7, A, 90, 5), new BagStack(8, A, 10, 2) };
        var r = Run(owned, 10, (A, 5, 99));
        Assert.False(r[0]!.Refused);
        Assert.Equal((short)2, r[0]!.MergeIntoSlot);   // slot 2 is lower than 5
        Assert.Equal(15, r[0]!.MergedCount);
        Assert.Empty(r[0]!.Opened);
    }

    [Fact]
    public void OverflowOpensFullStacksThenRemainder()
    {
        var r = Run(System.Array.Empty<BagStack>(), 10, (A, 250, 99));
        Assert.Null(r[0]!.MergeIntoSlot);
        Assert.Equal(new[] { ((short)0, 99), ((short)1, 99), ((short)2, 52) }, r[0]!.Opened);
    }

    [Fact]
    public void RefusedEntryConsumesNoCell_LaterEntryStillPlaces()
    {
        // one free cell; the 250x needs 3 => refused and consumes nothing; the 1x then takes cell 0.
        var r = Run(System.Array.Empty<BagStack>(), 1, (A, 250, 99), (B, 1, 1));
        Assert.True(r[0]!.Refused);
        Assert.False(r[1]!.Refused);
        Assert.Equal(new[] { ((short)0, 1) }, r[1]!.Opened);
    }

    [Fact]
    public void MergesIntoAStackOpenedByAnEarlierEntry()
    {
        var r = Run(System.Array.Empty<BagStack>(), 10, (A, 50, 99), (A, 30, 99));
        Assert.Equal(new[] { ((short)0, 50) }, r[0]!.Opened);
        Assert.Equal((short)0, r[1]!.MergeIntoSlot);   // folds into the stack opened at cell 0
        Assert.Equal(80, r[1]!.MergedCount);
        Assert.Empty(r[1]!.Opened);
    }

    [Fact]
    public void SkipsGoldAndNonPositiveEntries()
    {
        var r = Run(System.Array.Empty<BagStack>(), 10, (0u, 100, 1), (A, 0, 99));
        Assert.Null(r[0]);
        Assert.Null(r[1]);
    }

    [Fact]
    public void NonStackableOpensOneCellPerUnit()
    {
        var r = Run(System.Array.Empty<BagStack>(), 10, (A, 3, 1));
        Assert.Equal(new[] { ((short)0, 1), ((short)1, 1), ((short)2, 1) }, r[0]!.Opened);
    }
}
