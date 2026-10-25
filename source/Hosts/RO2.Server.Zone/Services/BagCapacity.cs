using RO2.Domain.Entities;
using RO2.GameData;
using RO2.Inventory.Domain;

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
/// The slot math itself lives in <see cref="BagLayout"/> (Inventory context); this class maps the inventory rows and item data onto it.
/// </para>
/// </remarks>
public static class BagCapacity
{
 /// <summary>Number of cells in the main bag (container 1).</summary>
    public const int MainBagCells = BagLayout.MainBagCells;

 /// <summary>Flat-slot stride between extra bags, which is also the largest supported Bag_Size.</summary>
    public const int ExtraBagStride = BagLayout.ExtraBagStride;

 /// <summary>Number of character-bag cells that can hold an extra bag.</summary>
    public const int ExtraBagCount = BagLayout.ExtraBagCount;

 /// <summary>
 /// Lists the free usable bag slots, lowest first: the main bag, then each equipped bag in character-bag order.
 /// </summary>
 /// <param name="all">Everything the character owns, of any storage type.</param>
 /// <param name="items">Item data, used for each equipped bag's Bag_Size.</param>
 /// <returns>Free flat slots, or an empty list when the bag is full.</returns>
    public static List<short> FreeCells(IEnumerable<InventoryItem> all, IReadOnlyDictionary<uint, ItemData> items)
    {
        var rows = all as IReadOnlyCollection<InventoryItem> ?? all.ToList();
        return BagLayout.FreeCells(OccupiedCells(rows), ExtraBagsOf(rows, items));
    }

 /// <summary>The flat cells that hold a placed bag row (storage type 0, slot 0 or more).</summary>
    public static IEnumerable<int> OccupiedCells(IEnumerable<InventoryItem> all)
        => all.Where(i => i.StorageType == 0 && i.Slot >= 0).Select(i => (int)i.Slot);

 /// <summary>The extra bags equipped in the character-bag cells (storage type 3) with the Bag_Size of their item data.</summary>
    public static List<ExtraBag> ExtraBagsOf(IEnumerable<InventoryItem> all, IReadOnlyDictionary<uint, ItemData> items)
        => all.Where(i => i.StorageType == 3 && i.Slot >= 0)
              .Select(i => new ExtraBag(i.Slot, items.TryGetValue(i.ItemId, out var data) ? data.Bag_Size : 0))
              .ToList();
}
