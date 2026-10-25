namespace RO2.Inventory.Domain;

/// <summary>
/// Pure per-character bank (storage) slot-capacity rules, over primitives and inventory rows. The count
/// starts at <see cref="Default"/> and grows by one <see cref="SlotStep"/> step per Storage Slot Expansion
/// item up to <see cref="Max"/>. It knows nothing of the character entity, the wire, or the game data:
/// callers pass the stored count and the stored rows in.
/// </summary>
public static class BankSlots
{
    /// <summary>Slot count of a bank that never expanded.</summary>
    public const byte Default = 20;

    /// <summary>Largest bank size. Expansion is refused at this count.</summary>
    public const byte Max = 120;

    /// <summary>Step the slot count is rounded to, equal to the slots added by one expansion item.</summary>
    public const byte SlotStep = 20;

    /// <summary>
    /// The slot count to report: the stored count, or <see cref="Default"/> when none is stored, clamped to
    /// <see cref="Max"/>.
    /// </summary>
    /// <param name="stored">The stored slot count (0 when none was ever stored).</param>
    public static byte SlotsOf(byte stored)
    {
        if (stored == 0) return Default;
        return Math.Min(stored, Max);
    }

    /// <summary>The slot count after using an expansion item, capped at <see cref="Max"/>.</summary>
    /// <param name="current">Current slot count.</param>
    /// <param name="bagSize">Slots added by the item.</param>
    public static byte Expanded(byte current, int bagSize) =>
        (byte)Math.Clamp(current + Math.Max(0, bagSize), 0, Max);

    /// <summary>
    /// The slot count a bank needs to hold its stored items, rounded up to the next <see cref="SlotStep"/>.
    /// Banks written before the count was stored may hold items past slot 19; the count is raised as needed
    /// so every stored item stays reachable.
    /// </summary>
    /// <param name="stored">Stored slot count.</param>
    /// <param name="items">Items held by the bank (bank rows are <c>StorageType</c> 2). May be null.</param>
    public static byte RequiredFor(byte stored, IEnumerable<InventoryItem>? items)
    {
        var slots = stored == 0 ? Default : Math.Min(stored, Max);
        if (items is null) return slots;
        int highest = -1;
        foreach (var it in items)
            if (it.StorageType == 2 && it.Slot > highest) highest = it.Slot;
        if (highest < slots) return slots;
        int needed = (highest + 1 + SlotStep - 1) / SlotStep * SlotStep;
        return (byte)Math.Min(needed, Max);
    }
}
