using RO2.Domain.Entities;
using RO2.Network.Packets;
using RO2.Inventory.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Builds the seller's own item list for the private-shop setup window: 2648 AnsDeletePrivateShopItem, the answer to
/// a delete request.
/// </summary>
/// <remarks>
/// The success form is a compact-prefixed body of 58 header bytes followed by one 153-byte entry per remaining listing.
/// A non-zero result shows an error and deselects the item. On success the client clears its seller item list and
/// rebuilds it from the entries, using the count, bag, slot and uid of each entry. Zero-count entries are skipped.
/// </remarks>
internal static class PrivateShopItemPackets
{
 /// <summary>Blob bytes before the entries: result, title, count and 12 reserved bytes.</summary>
    internal const int ItemsOffset = 2 + PrivateShopRegistry.NameBytes + 2 + 12;

 /// <summary>One entry: 27 bytes of listing followed by the 126-byte item record, zeros in this message.</summary>
    internal const int EntryBytes = 27 + ItemPackets.ItemRecordLength;

 /// <summary>One row of the seller's list, with every field taken from server state.</summary>
 /// <param name="Index">The list index given by the listing (<c>PrivateShopRegistry.Listing.Index</c>); sent back by the delete request.</param>
 /// <param name="ItemId">ItemInfo template id.</param>
 /// <param name="Bag">The item's client bag.</param>
 /// <param name="Slot">The item's client slot.</param>
 /// <param name="Count">The listed count. Never 0.</param>
 /// <param name="Uid">The item instance uid.</param>
 /// <param name="Price">The listed price.</param>
    internal sealed record SellerRow(byte Index, uint ItemId, byte Bag, byte Slot, ushort Count, ulong Uid, ulong Price);

 /// <summary>
 /// Builds the rows for the seller's live listings (<see cref="PrivateShopRegistry.Live"/>) in listing order, so the
 /// seller's window and the buyers' listings match.
 /// </summary>
    internal static List<SellerRow> SellerRows(IEnumerable<(PrivateShopRegistry.Listing Listing, InventoryItem Item)> live)
    {
        var rows = new List<SellerRow>();
        foreach (var (listing, item) in live)
        {
            var (bag, slot) = ItemWorkbench.ClientBagSlot(item);
            rows.Add(new SellerRow(listing.Index, item.ItemId, bag, slot, listing.Count, item.Id, listing.Price));
        }
        return rows;
    }

 /// <summary>
 /// Builds the 2648 AnsDeletePrivateShopItem success body: <c>[compactLen][u16 result 0][wchar_t[21] title][u16 count]
 /// [12 zero bytes][count x 153-byte entry]</c>.
 /// </summary>
 /// <remarks>
 /// The body is compact-prefixed like 2657 and 2658. The title is zero during setup.
 /// </remarks>
    internal static byte[] BuildAnsDeletePrivateShopItem2648(ReadOnlySpan<byte> title42, IReadOnlyList<SellerRow> rows)
    {
        if (title42.Length != PrivateShopRegistry.NameBytes)
            throw new ArgumentException($"The shop title is a {PrivateShopRegistry.NameBytes}-byte wchar_t[21] field, got {title42.Length}.");
        int blobLength = ItemsOffset + EntryBytes * rows.Count;
        using var w = new PacketWriter(blobLength + 3);
        w.WriteCompactLength(blobLength);
        w.WriteUInt16(0);                           
        w.WriteBytes(title42);                      
        w.WriteUInt16((ushort)rows.Count);          
        w.WriteZeros(12);                           
        foreach (var row in rows)
        {
            w.WriteByte(row.Index);                 
            w.WriteUInt32(row.ItemId);              
            w.WriteByte(row.Bag);                   
            w.WriteByte(row.Slot);                  
            w.WriteUInt16(row.Count);               
            w.WriteUInt16(row.Count);               
            w.WriteUInt64(row.Uid);                 
            w.WriteUInt64(row.Price);               
            w.WriteZeros(ItemPackets.ItemRecordLength); 
        }
        return w.ToArray();
    }
}
