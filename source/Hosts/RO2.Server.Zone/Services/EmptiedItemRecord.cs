using RO2.Domain.Entities;

namespace RO2.Server.Zone.Services;

/// <summary>
/// The 126-byte item record sent in an OnDestroyItem (2379) that empties a cell (count 0):
/// <c>[u64 uid][u32 template][u16 0][u8 count 0][u8 bag][u8 slot][109 x 0]</c>. Only the uid, the template and the cell
/// (bag and slot) are populated; every other byte, durability, bind flags and refine grade included, is zero.
/// </summary>
/// <remarks>
/// <para>
/// Used for removal reasons 0, 1 and 2. The card book put (reason 5, see
/// <see cref="QuestProgressPackets.ItemRemovedReasonCardBookPut"/>) keeps the full record and must not use this type.
/// </para>
/// </remarks>
internal static class EmptiedItemRecord
{
    /// <summary>Offset of the bag byte in the record.</summary>
    private const int BagOffset = 0x0F;

    /// <summary>Offset of the slot byte in the record.</summary>
    private const int SlotOffset = 0x10;

    /// <summary>Bytes kept from the start of the record: the u64 uid and the u32 template.</summary>
    private const int IdentityLength = 12;

    /// <summary>
    /// The emptied-cell record of <paramref name="item"/>: its uid, template and cell (bag and slot), with all other bytes zero.
    /// </summary>
    public static byte[] Build(InventoryItem item)
    {
        var full = WorldEntryService.BuildItemRecord126(item);
        var rec = new byte[ItemPackets.ItemRecordLength];
        full.AsSpan(0, IdentityLength).CopyTo(rec);   // +0x00 uid, +0x08 template
        rec[BagOffset] = full[BagOffset];             // +0x0F bag
        rec[SlotOffset] = full[SlotOffset];           // +0x10 slot
        return rec;                                   // +0x0E count stays 0: the client empties the cell
    }
}
