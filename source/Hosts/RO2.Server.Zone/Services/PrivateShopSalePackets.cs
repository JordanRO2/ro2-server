using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// The seller's side of a private-shop item sale: 2655 NfyPrivateShopItemSaled (remaining list and wallet) and
/// 2656 NfyPrivateShopSaledList (the items just sold).
/// </summary>
internal static class PrivateShopSalePackets
{
 /// <summary>
 /// Builds the 2655 NfyPrivateShopItemSaled body for the seller: <c>[compactLen][u16 0][wchar_t[21] title][u16 n]
 /// [u32 buyer handle][u64 seller wallet after][n x 153-byte entry]</c>, the listings still on sale.
 /// </summary>
 /// <remarks>
 /// The client ignores the notice when the buyer handle is its own player. When no items remain and no craft is
 /// offered, the client ends the private shop.
 /// </remarks>
    internal static byte[] BuildItemSaled2655(ReadOnlySpan<byte> title42, uint buyerHandle, long sellerZenyAfter,
        IReadOnlyList<PrivateShopItemListPackets.Entry> remaining)
    {
        if (title42.Length != PrivateShopRegistry.NameBytes)
            throw new ArgumentException($"The shop title is a {PrivateShopRegistry.NameBytes}-byte wchar_t[21] field, got {title42.Length}.");
        int blob = PrivateShopItemListPackets.ListHeaderBytes + PrivateShopItemListPackets.EntryBytes * remaining.Count;
        using var w = new PacketWriter(blob + 9);
        w.WriteCompactLength(blob);
        w.WriteUInt16(0);                            
        w.WriteBytes(title42);                       
        w.WriteUInt16((ushort)remaining.Count);      
        w.WriteUInt32(buyerHandle);                  
        w.WriteUInt64((ulong)sellerZenyAfter);       
        foreach (var e in remaining) PrivateShopItemListPackets.WriteEntry(w, e);
        return w.ToArray();
    }

 /// <summary>
 /// Builds the 2656 NfyPrivateShopSaledList body for the seller: <c>[compactLen][u32 buyer handle][u16 n][n x u32 item id]</c>.
 /// </summary>
 /// <remarks>
 /// The client resolves each item id to its name and shows a sale line naming the item and the buyer. It changes no state.
 /// </remarks>
    internal static byte[] BuildSaledList2656(uint buyerHandle, IReadOnlyList<uint> soldItemIds)
    {
        int blob = 4 + 2 + 4 * soldItemIds.Count;
        using var w = new PacketWriter(blob + 9);
        w.WriteCompactLength(blob);
        w.WriteUInt32(buyerHandle);                  
        w.WriteUInt16((ushort)soldItemIds.Count);    
        foreach (var id in soldItemIds) w.WriteUInt32(id); 
        return w.ToArray();
    }
}
