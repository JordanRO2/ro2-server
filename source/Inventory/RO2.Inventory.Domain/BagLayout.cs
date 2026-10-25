namespace RO2.Inventory.Domain;

/// <summary>An extra bag equipped in character-bag cell <see cref="BagCell"/> and the cell count its item data gives it.</summary>
/// <param name="BagCell">The character-bag cell the bag is equipped in (0..<see cref="BagLayout.ExtraBagCount"/> - 1 are usable).</param>
/// <param name="BagSize">The item's raw Bag_Size; <see cref="BagLayout"/> clamps it to 0..<see cref="BagLayout.ExtraBagStride"/>.</param>
public readonly record struct ExtraBag(int BagCell, int BagSize);

/// <summary>A row that keeps or moves in a slot repair: the row uid and the cell it is to end up in.</summary>
/// <param name="Id">The row uid.</param>
/// <param name="Slot">The cell the row holds after the repair.</param>
public readonly record struct SlotAssignment(ulong Id, short Slot);

/// <summary>
/// Pure bag slot geometry and free-cell math over primitives, in the flat-slot space written to the wire. Flat
/// slots 0..19 are the main bag; flat slot 20 + 35 * n + k is cell k of the extra bag equipped in character-bag
/// cell n, and exists only while that bag is equipped. It knows nothing of the inventory row type or the game data:
/// callers pass the occupied cells and the equipped bags in.
/// </summary>
public static class BagLayout
{
    /// <summary>Number of cells in the main bag.</summary>
    public const int MainBagCells = 20;

    /// <summary>Flat-slot stride between extra bags, which is also the largest supported Bag_Size.</summary>
    public const int ExtraBagStride = 35;

    /// <summary>Number of character-bag cells that can hold an extra bag.</summary>
    public const int ExtraBagCount = 3;

    /// <summary>Clamps a raw Bag_Size to the cells an extra bag can provide (0..<see cref="ExtraBagStride"/>).</summary>
    public static int ClampBagSize(int bagSize) => System.Math.Clamp(bagSize, 0, ExtraBagStride);

    /// <summary>
    /// The free usable bag cells, lowest first: the main bag cells 0..19, then each equipped bag in character-bag
    /// cell order, each up to its clamped size at flat 20 + 35 * cell + k. A bag outside the usable character-bag
    /// cells provides nothing. Empty when every usable cell is taken.
    /// </summary>
    /// <param name="occupied">The flat cells already holding a bag row (negative values are ignored).</param>
    /// <param name="extraBags">The equipped extra bags; may be in any order.</param>
    public static List<short> FreeCells(IEnumerable<int> occupied, IEnumerable<ExtraBag> extraBags)
        => EnumerateFreeCells(new HashSet<int>(occupied), extraBags).ToList();

    /// <summary>The lowest free usable bag cell (the first of <see cref="FreeCells"/>), or -1 when every one is taken.</summary>
    public static short FirstFreeCell(IEnumerable<int> occupied, IEnumerable<ExtraBag> extraBags)
    {
        foreach (var cell in EnumerateFreeCells(new HashSet<int>(occupied), extraBags)) return cell;
        return -1;
    }

    /// <summary>
    /// The requested cell when it is free and usable, otherwise the lowest free usable cell, or -1 when the main bag
    /// and every equipped bag are full.
    /// </summary>
    /// <param name="occupied">The flat cells already holding a bag row.</param>
    /// <param name="extraBags">The equipped extra bags.</param>
    /// <param name="requested">The cell asked for; negative means no preference.</param>
    public static short ChooseCell(IEnumerable<int> occupied, IReadOnlyList<ExtraBag> extraBags, int requested)
    {
        var taken = new HashSet<int>(occupied);
        if (requested >= 0 && !taken.Contains(requested) && IsUsableCell(requested, extraBags))
            return (short)requested;
        foreach (var cell in EnumerateFreeCells(taken, extraBags)) return cell;
        return -1;
    }

    /// <summary>
    /// True when the flat cell exists: a main bag cell, or a cell below the clamped size of the first bag equipped in
    /// its character-bag cell.
    /// </summary>
    public static bool IsUsableCell(int flat, IEnumerable<ExtraBag> extraBags)
    {
        if (flat < MainBagCells) return true;
        int n = (flat - MainBagCells) / ExtraBagStride, k = (flat - MainBagCells) % ExtraBagStride;
        if (n >= ExtraBagCount) return false;
        foreach (var bag in extraBags)
            if (bag.BagCell == n) return k < ClampBagSize(bag.BagSize);
        return false;
    }

    /// <summary>The lowest slot index (from 0) that is not in <paramref name="occupied"/>; negative entries never block.</summary>
    public static short LowestFreeSlot(IEnumerable<int> occupied)
    {
        var taken = new HashSet<int>(occupied);
        short slot = 0;
        while (taken.Contains(slot))
            slot++;
        return slot;
    }

    /// <summary>
    /// The placement decision of a slot repair for one storage type: rows go in ascending slot order (unplaced, Slot
    /// below 0, last) then by uid, the first claimant of a slot keeps it, and every duplicate or unplaced row moves to
    /// the next free index counting up from 0. Returns only the rows that move, in processing order.
    /// </summary>
    /// <param name="rows">The rows of one storage type with their current slots.</param>
    public static IReadOnlyList<SlotAssignment> PlanSlotRepair(IEnumerable<(ulong Id, short Slot)> rows)
    {
        var moves = new List<SlotAssignment>();
        var occupied = new HashSet<int>();
        short next = 0;
        foreach (var row in rows.OrderBy(r => r.Slot < 0 ? int.MaxValue : (int)r.Slot).ThenBy(r => r.Id))
        {
            if (row.Slot >= 0 && occupied.Add((int)row.Slot))
                continue;                                    // first claimant keeps its (valid, unique) slot
            while (occupied.Contains(next)) next++;
            occupied.Add(next);
            moves.Add(new SlotAssignment(row.Id, next));
            next++;
        }
        return moves;
    }

    private static IEnumerable<short> EnumerateFreeCells(HashSet<int> used, IEnumerable<ExtraBag> extraBags)
    {
        for (int f = 0; f < MainBagCells; f++)
            if (!used.Contains(f)) yield return (short)f;

        foreach (var bag in extraBags.Where(b => b.BagCell >= 0 && b.BagCell < ExtraBagCount).OrderBy(b => b.BagCell))
        {
            int size = ClampBagSize(bag.BagSize);
            for (int k = 0; k < size; k++)
            {
                int f = MainBagCells + ExtraBagStride * bag.BagCell + k;
                if (!used.Contains(f)) yield return (short)f;
            }
        }
    }
}
