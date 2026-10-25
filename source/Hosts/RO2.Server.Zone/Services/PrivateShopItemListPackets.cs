using RO2.Domain.Entities;
using RO2.Network.Packets;
using RO2.Inventory.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Builds the private-shop item listing entry as the client stores it. The entry is shared by 2657 (buyer list on open),
/// 2662 (buyer list after a sale) and 2655 (seller notice of a sale).
/// </summary>
/// <remarks>
/// Entry layout: <c>[u8 list index][u32 item id][u8][u8][u16 count][u16 count][u64 uid][u64 price][126-byte item record]</c>,
/// 153 bytes in total. The two count fields hold the listed count. The seller's entry also carries the item's bag and
/// slot in the two unnamed bytes; the buyer's entry leaves them zero.
/// </remarks>
internal static class PrivateShopItemListPackets
{
 /// <summary>Blob bytes before the first entry of 2657, 2662 and 2655: result, title, count and 12 reserved bytes.</summary>
    internal const int ListHeaderBytes = 2 + PrivateShopRegistry.NameBytes + 2 + 12;

 /// <summary>One entry: 27 bytes of listing followed by the 126-byte item record.</summary>
    internal const int EntryBytes = 27 + ItemPackets.ItemRecordLength;

 /// <summary>One listing entry, in wire order. <c>Bag</c> and <c>Slot</c> are 0 in a buyer's list.</summary>
    internal readonly record struct Entry(byte Index, uint ItemId, byte Bag, byte Slot, ushort Count, ulong Uid,
        ulong Price, byte[] Record126);

 /// <summary>
 /// A listing as a buyer's list carries it (2657 on open, 2662 after a sale). The bag and slot bytes are 0, and the
 /// record's count is the listed count, not the full stack count.
 /// </summary>
 /// <remarks>
 /// The buyer's client takes the quantity from the item record, so the record's count is the listed count.
 /// </remarks>
    internal static Entry BuyerEntry(PrivateShopRegistry.Listing listing, InventoryItem item)
    {
        var record = WorldEntryService.BuildItemRecord126(item);
        record[14] = (byte)Math.Min((int)listing.Count, byte.MaxValue); 
        return new(listing.Index, item.ItemId, 0, 0, listing.Count, item.Id, listing.Price, record);
    }

 /// <summary>A listing as the seller's own list carries it (2655), with the item's bag and slot.</summary>
    internal static Entry SellerEntry(PrivateShopRegistry.Listing listing, InventoryItem item)
    {
        var (bag, slot) = ItemWorkbench.ClientBagSlot(item);
        return new(listing.Index, item.ItemId, bag, slot, listing.Count, item.Id, listing.Price,
            WorldEntryService.BuildItemRecord126(item));
    }

 /// <summary>Writes one 153-byte entry.</summary>
    internal static void WriteEntry(PacketWriter w, in Entry e)
    {
        if (e.Record126 is not { Length: ItemPackets.ItemRecordLength })
            throw new ArgumentException($"A shop entry carries a {ItemPackets.ItemRecordLength}-byte item record.", nameof(e));
        w.WriteByte(e.Index);        
        w.WriteUInt32(e.ItemId);     
        w.WriteByte(e.Bag);          
        w.WriteByte(e.Slot);         
        w.WriteUInt16(e.Count);      
        w.WriteUInt16(e.Count);      
        w.WriteUInt64(e.Uid);        
        w.WriteUInt64(e.Price);      
        w.WriteBytes(e.Record126);   
    }

 /// <summary>
 /// Builds the 2662 update of a buyer's list after a sale: <c>[compactLen][u16 0][42 zero bytes][u16 count][u32 buyer handle]
 /// [8 zero bytes][count x 153-byte entry]</c>, the shop's remaining listings.
 /// </summary>
 /// <remarks>
 /// The title is zero in this message. The buyer handle identifies the buyer, whose cart is cleared when it matches the
 /// receiver. When the count is 0 and the buyer has nothing required, the buyer's window closes.
 /// </remarks>
    internal static byte[] BuildItemListUpdate2662(uint buyerHandle, IReadOnlyList<Entry> entries)
    {
        int blob = ListHeaderBytes + EntryBytes * entries.Count;
        using var w = new PacketWriter(blob + 9);
        w.WriteCompactLength(blob);
        w.WriteUInt16(0);                        
        w.WriteZeros(PrivateShopRegistry.NameBytes); 
        w.WriteUInt16((ushort)entries.Count);    
        w.WriteUInt32(buyerHandle);              
        w.WriteZeros(8);                         
        foreach (var e in entries) WriteEntry(w, e);
        return w.ToArray();
    }
}
