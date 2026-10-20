using RO2.Domain.Entities;
using RO2.Domain.GameData;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Determines which bag cells a character can use, in the flat-slot space written to the wire.
/// </summary>
/// <remarks>
/// <para>
/// Flat slots 0..19 are the main bag. Flat slot 20 + 35 * n + k is slot k of the extra bag equipped in character-bag cell n, and exists only while that bag is equipped. Each extra bag provides its ItemInfo Bag_Size cells.
/// </para>
/// <para>
/// Inventory insertion uses this to refuse new items when the bag is full, instead of placing them in cells the character does not own.
/// </para>
/// <para>
/// Other handlers duplicate this search (FirstFreeBagCell); keep them in sync.
/// </para>
/// </remarks>
public static class BagCapacity
{
 /// <summary>Number of cells in the main bag (container 1).</summary>
    public const int MainBagCells = 20;

 /// <summary>Flat-slot stride between extra bags, which is also the largest supported Bag_Size.</summary>
    public const int ExtraBagStride = 35;

 /// <summary>Number of character-bag cells that can hold an extra bag.</summary>
    public const int ExtraBagCount = 3;

 /// <summary>
 /// Lists the free usable bag slots, lowest first: the main bag, then each equipped bag in character-bag order.
 /// </summary>
 /// <param name="all">Everything the character owns, of any storage type.</param>
 /// <param name="items">Item data, used for each equipped bag's Bag_Size.</param>
 /// <returns>Free flat slots, or an empty list when the bag is full.</returns>
    public static List<short> FreeCells(IEnumerable<InventoryItem> all, IReadOnlyDictionary<uint, ItemData> items)
    {
        var rows = all as IReadOnlyCollection<InventoryItem> ?? all.ToList();
        var used = new HashSet<int>(rows.Where(i => i.StorageType == 0 && i.Slot >= 0).Select(i => (int)i.Slot));
        var free = new List<short>();

        for (int f = 0; f < MainBagCells; f++)
            if (!used.Contains(f)) free.Add((short)f);

        foreach (var bag in rows.Where(i => i.StorageType == 3 && i.Slot >= 0 && i.Slot < ExtraBagCount).OrderBy(i => i.Slot))
        {
            int size = items.TryGetValue(bag.ItemId, out var data) ? Math.Clamp(data.Bag_Size, 0, ExtraBagStride) : 0;
            for (int k = 0; k < size; k++)
            {
                int f = MainBagCells + ExtraBagStride * bag.Slot + k;
                if (!used.Contains(f)) free.Add((short)f);
            }
        }
        return free;
    }
}
