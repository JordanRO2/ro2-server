using RO2.Economy.Domain;
using RO2.Domain.Entities;
using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Wire builders for crafting sales from a private shop: the buyer's craft list (2658), the buyer's answer to a
/// craft purchase (2660), and the seller's sale notice (2654).
/// </summary>
internal static class PrivateShopCraftPackets
{
 /// <summary>Size of the 2658 header before the entries: result, title and entry count.</summary>
    internal const int CraftListHeaderBytes = 2 + PrivateShopRegistry.NameBytes + 2;

 /// <summary>Size of one 2658 entry: u16 craft id and u64 price.</summary>
    internal const int CraftEntryBytes = 2 + 8;

 /// <summary>Body length of the 2660 message.</summary>
    internal const int BuyCraftAnswerBytes = 16;

 /// <summary>Body length of the 2654 message.</summary>
    internal const int CraftSaleNoticeBytes = 18;

 /// <summary>
 /// Builds the 2658 craft list: <c>[compactLen][u16 result 0][wchar_t[21] title][u16 count][count x {u16 craftId, u64 price}]</c>.
 /// The offers are listed in the seller's order.
 /// </summary>
    internal static byte[] BuildBuyerCraftList2658(ReadOnlySpan<byte> title42, IReadOnlyList<PrivateShopCraftOffer> offers)
    {
        if (title42.Length != PrivateShopRegistry.NameBytes)
            throw new ArgumentException($"The shop title is a {PrivateShopRegistry.NameBytes}-byte wchar_t[21] field, got {title42.Length}.");
        using var w = new PacketWriter(CraftListHeaderBytes + 3 + CraftEntryBytes * offers.Count);
        w.WriteCompactLength(CraftListHeaderBytes + CraftEntryBytes * offers.Count);
        w.WriteUInt16(0);                       
        w.WriteBytes(title42);                  
        w.WriteUInt16((ushort)offers.Count);    
        foreach (var offer in offers)
        {
            w.WriteUInt16(offer.CraftId);       
            w.WriteUInt64(offer.Price);         
        }
        return w.ToArray();
    }

 /// <summary>
 /// Builds the 2660 purchase answer, 16 bytes: <c>[u16 result 0][u16 craftId][u64 buyer zeny after paying][u32 crafted item id]</c>.
 /// </summary>
 /// <remarks>
 /// The zeny value decreases by the offer price on each purchase. The crafted item id is the item that the craft produces.
 /// A non-zero result is reported to the client as an error.
 /// </remarks>
    internal static byte[] BuildBuyPrivateShopCraft2660(ushort craftId, long buyerZenyAfter, uint craftedItemId)
    {
        using var w = new PacketWriter(BuyCraftAnswerBytes);
        w.WriteUInt16(0);                        
        w.WriteUInt16(craftId);                  
        w.WriteUInt64((ulong)buyerZenyAfter);    
        w.WriteUInt32(craftedItemId);            
        return w.ToArray();
    }

 /// <summary>
 /// Builds the 2654 sale notice sent to the seller, 18 bytes: <c>[u16 craftId][u32 buyer handle][u64 seller zeny after the sale][u32 0]</c>.
 /// </summary>
 /// <remarks>
 /// The buyer handle is the purchasing character's entity handle. The client ignores the notice when the buyer is not known.
 /// </remarks>
    internal static byte[] BuildCraftSaleShopItemBuy2654(ushort craftId, uint buyerHandle, long sellerZenyAfter)
    {
        using var w = new PacketWriter(CraftSaleNoticeBytes);
        w.WriteUInt16(craftId);                  
        w.WriteUInt32(buyerHandle);              
        w.WriteUInt64((ulong)sellerZenyAfter);   
        w.WriteUInt32(0);                        
        return w.ToArray();
    }
}
