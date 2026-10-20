using RO2.Domain.Entities;
using RO2.Domain.GameData;

namespace RO2.Server.Zone.Services;

/// <summary>Reason a placement is refused by <see cref="EquipRules.CanPlace"/>.</summary>
public enum EquipRefusal
{
    None = 0,
    UnknownTemplate,
 /// <summary>The storage type is not bag, worn, bank or bag window.</summary>
    IllegalStorageType,
 /// <summary>The cell index is outside the container.</summary>
    SlotOutOfRange,
 /// <summary>The item has no worn slot, or is not a bag for the bag window.</summary>
    NotEquippable,
 /// <summary>The cell is not one that the item's equip slot may occupy.</summary>
    WrongSlot,
    WrongEquipType,
    WrongItemType,
    LevelTooLow,
    JobNotAllowed,
    SexNotAllowed,
 /// <summary>Another item already occupies the destination cell.</summary>
    DestinationOccupied,
 /// <summary>A costume's overlap mask covers a slot that another worn item holds.</summary>
    OverlapConflict,
}

/// <summary>The player attributes that <see cref="EquipRules"/> needs. <c>Sex</c> is the value of Require_Sex (1 male, 2 female).</summary>
public readonly record struct EquipActor(int Level, int Job, int Sex);

/// <summary>Checks whether an item may be placed in a destination cell for a given player.</summary>
/// <remarks>
/// <para>
/// Cell coordinates are flat: storage 1 uses the worn slot (0 to 49), storage 3 uses slots 0 to 2 for the equipped bags,
/// storage 0 is the bag and storage 2 is the bank.
/// </para>
/// <para>
/// The worn slot is not always the item's equip slot. Rings (Equip_Slot 10) may be worn in worn cell 10 or 11, and every
/// other item stays in its own cell. Worn slots are: armour 1 to 5, weapons 6 and 7, accessories 8 to 12, quiver 13,
/// costumes 14 to 19, and cards 20 and above. <see cref="WornSlotsFor"/> is the single table for this mapping.
/// </para>
/// <para>
/// A ring may be placed in either ring cell, at the player's choice.
/// </para>
/// <para>
/// Cards wear in slots 20 to 24, and an equipped bag is in storage 3. Equip_Type is checked only where the data is
/// uniform. Gathering tools are legal main-hand items.
/// </para>
/// <para>
/// Equip_Slot_Overlap is a bit mask of the costume layers an item covers. The overlap check is disabled by default and
/// runs only when the caller passes <c>enforceOverlap: true</c>. The live swap and move handlers do not enable it.
/// </para>
/// <para>
/// For a swap, pass the swap partner's uid as <c>vacatingUid</c> so that the vacated cell is not counted as occupied.
/// Stack merges are not placements.
/// </para>
/// </remarks>
public static class EquipRules
{
 /// <summary>Costume slot named by each bit of Equip_Slot_Overlap, bit 0 first.</summary>
    public static readonly byte[] OverlapBitSlots = { 14, 15, 19, 16, 17, 18, 25, 26 };

    public const int MaxBagCellSlot = 124;   
    public const int MaxBankSlot = 119;      
    public const int MaxWornSlot = 49;
    public const int MaxBagWindowSlot = 2;

    private static readonly int[] NoSlots = Array.Empty<int>();
    private static readonly int[] CardSlots = { 20, 21, 22, 23, 24 };
    private static readonly int[] RingSlots = { 10, 11 };

 /// <summary>Worn slots (storage 1) that the template may occupy. Empty when it cannot be worn.</summary>
    public static IReadOnlyList<int> WornSlotsFor(ItemData t)
    {
        if (t.Equip_Slot == 0)
            return t.Item_Type is 13 or 139 ? CardSlots : NoSlots;
        if (t.Equip_Slot == 10)
            return RingSlots;   
        return t.Equip_Slot switch
        {
            >= 1 and <= 9 or 12 or 13 or (>= 14 and <= 19) or 25 or 26 => new[] { (int)t.Equip_Slot },
            _ => NoSlots,   
        };
    }

    public static EquipRefusal CanPlace(InventoryItem item, ItemData? template, byte storageType, int slot,
        EquipActor player, IReadOnlyCollection<InventoryItem> currentRows, ulong vacatingUid = 0,
        bool enforceOverlap = false)
    {
        if (template is null) return EquipRefusal.UnknownTemplate;

        int max;
        switch (storageType)
        {
            case 0: max = MaxBagCellSlot; break;
            case 1: max = MaxWornSlot; break;
            case 2: max = MaxBankSlot; break;
            case 3: max = MaxBagWindowSlot; break;
            default: return EquipRefusal.IllegalStorageType;
        }
        if (slot < 0 || slot > max) return EquipRefusal.SlotOutOfRange;

        if (storageType == 1)
        {
            var r = CheckWorn(template, slot, player);
            if (r != EquipRefusal.None) return r;
        }
        else if (storageType == 3)
        {
            if (template.Item_Type != 7) return EquipRefusal.NotEquippable;
            var r = CheckRequirements(template, player);
            if (r != EquipRefusal.None) return r;
        }

        foreach (var row in currentRows)
            if (row.StorageType == storageType && row.Slot == slot && row.Id != item.Id && row.Id != vacatingUid)
                return EquipRefusal.DestinationOccupied;

        
        
        
        
        if (storageType == 1 && enforceOverlap && template.Equip_Slot_Overlap != 0)
        {
            for (int bit = 0; bit < OverlapBitSlots.Length; bit++)
            {
                if ((template.Equip_Slot_Overlap & (1 << bit)) == 0) continue;
                int covered = OverlapBitSlots[bit];
                if (covered == slot) continue;
                foreach (var row in currentRows)
                    if (row.StorageType == 1 && row.Slot == covered && row.Id != item.Id && row.Id != vacatingUid)
                        return EquipRefusal.OverlapConflict;
            }
        }
        return EquipRefusal.None;
    }

    private static EquipRefusal CheckWorn(ItemData t, int slot, EquipActor player)
    {
        var worn = WornSlotsFor(t);
        if (worn.Count == 0) return EquipRefusal.NotEquippable;
        if (!Contains(worn, slot)) return EquipRefusal.WrongSlot;
        if (!ItemTypeFits(t)) return EquipRefusal.WrongItemType;
        if (!EquipTypeFits(t)) return EquipRefusal.WrongEquipType;
        return CheckRequirements(t, player);
    }

    private static bool Contains(IReadOnlyList<int> list, int v)
    {
        for (int i = 0; i < list.Count; i++)
            if (list[i] == v) return true;
        return false;
    }

    private static EquipRefusal CheckRequirements(ItemData t, EquipActor p)
    {
        if (p.Level < t.Require_Level) return EquipRefusal.LevelTooLow;
        if (t.Require_Job != 0 && (p.Job < 1 || p.Job > 63 || (t.Require_Job & (1L << (p.Job - 1))) == 0))
            return EquipRefusal.JobNotAllowed;
        if (t.Require_Sex is 1 or 2 && p.Sex != t.Require_Sex) return EquipRefusal.SexNotAllowed;
        return EquipRefusal.None;
    }

    private static bool ItemTypeFits(ItemData t) => t.Equip_Slot switch
    {
        0 => t.Item_Type is 13 or 139,
        >= 1 and <= 5 => t.Item_Type == 2,
        6 or 7 => t.Item_Type is 1 or 6,
        13 => t.Item_Type == 104,
        _ => t.Item_Type is 3 or 4 or 52,
    };

    private static bool EquipTypeFits(ItemData t) => t.Equip_Slot switch
    {
        >= 1 and <= 5 => t.Equip_Type == 18 + t.Equip_Slot,   
        6 => t.Equip_Type is 1 or 2 or 3 or 4 or 9 or 10 or 13 or 14 or 16 or 39 or 40,
        7 => t.Equip_Type is 11 or 17 or 38 or 42,
        8 => t.Equip_Type == 24,
        9 => t.Equip_Type == 25,
        10 => t.Equip_Type == 26,
        12 => t.Equip_Type == 27,
        13 => t.Equip_Type == 18,
        _ => true,   
    };
}
