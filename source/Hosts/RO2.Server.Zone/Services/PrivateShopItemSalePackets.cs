using System.Buffers.Binary;
using RO2.Economy.Domain;
using RO2.Network.Packets;

namespace RO2.Server.Zone.Services;

/// <summary>
/// The buyer's side of a private-shop item purchase: the 1482 ReqBuyPrivateShopItem request and its answer,
/// 2659 AnsBuyPrivateShopItem, including the refusal codes.
/// </summary>
internal static class PrivateShopItemSalePackets
{
 /// <summary>2659 success body before the echoed indices: u16 result, u64 paid, u64 buyer wallet and u16 index count.</summary>
    internal const int BuyAnswerBlobBytes = 2 + 8 + 8 + 2;

 /// <summary>2659 refusal body: the u16 result code alone.</summary>
    internal const int BuyRefusalBlobBytes = 2;

    
    
    
 /// <summary>Refusal: an item on the purchased list was already sold to another buyer.</summary>
    internal const ushort ResultAlreadySold = StallCodes.ResultAlreadySold;

 /// <summary>Refusal: the buyer is not the buyer this private shop is open to.</summary>
    internal const ushort ResultWrongBuyer = StallCodes.ResultWrongBuyer;

 /// <summary>Refusal: the buyer does not have enough room in the bags.</summary>
    internal const ushort ResultNoBagRoom = StallCodes.ResultNoBagRoom;

 /// <summary>Refusal: the seller's total zeny would exceed the limit.</summary>
    internal const ushort ResultSellerMoneyLimit = StallCodes.ResultSellerMoneyLimit;

 /// <summary>Refusal: the private shop has closed.</summary>
    internal const ushort ResultShopClosed = StallCodes.ResultShopClosed;

 /// <summary>Refusal: the buyer does not have enough zeny.</summary>
    internal const ushort ResultNotEnoughMoney = StallCodes.ResultNotEnoughMoney;

 /// <summary>
 /// Reads a 1482 ReqBuyPrivateShopItem body: <c>[u8 1][u8 6+n][blob]</c>, where the blob is
 /// <c>[u32 shop owner handle][u16 n][n x u8 list index]</c>. Returns false when the envelope or the blob is short.
 /// </summary>
 /// <remarks>
 /// The envelope is parsed leniently: the declared length is not compared, and the blob is bounded by the 6 + n bytes it declares.
 /// </remarks>
    internal static bool TryParseBuyRequest1482(ReadOnlySpan<byte> body, out uint ownerHandle, out byte[] indices)
    {
        ownerHandle = 0;
        indices = [];
        if (!body.TryGetCompactBlob(CompactLengthCheck.Lenient, out var blob) || blob.Length < 6) return false;
        int n = BinaryPrimitives.ReadUInt16LittleEndian(blob.Slice(4, 2));
        if (blob.Length < 6 + n) return false;
        ownerHandle = BinaryPrimitives.ReadUInt32LittleEndian(blob);
        indices = blob.Slice(6, n).ToArray();
        return true;
    }

 /// <summary>
 /// Builds the 2659 AnsBuyPrivateShopItem success body: <c>[01 20+n][u16 0][u64 zeny paid][u64 buyer wallet after]
 /// [u16 n][n x u8 list index]</c>. The index tail is copied from the request.
 /// </summary>
 /// <remarks>
 /// The echoed indices are written in the request's order, so <paramref name="requestIndices"/> is the raw index bytes of the
 /// request, not the distinct set that was bought. The client shows the paid amount and updates the wallet from this body.
 /// </remarks>
    internal static byte[] BuildBuyAnswer2659(ulong zenyPaid, long buyerZenyAfter, ReadOnlySpan<byte> requestIndices)
    {
        if (requestIndices.Length > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(requestIndices), "The request's index count is a u16.");
        int blob = BuyAnswerBlobBytes + requestIndices.Length;
        using var w = new PacketWriter(3 + blob);
        w.WriteCompactLength(blob);
        w.WriteUInt16(0);                                   
        w.WriteUInt64(zenyPaid);                            
        w.WriteUInt64((ulong)buyerZenyAfter);               
        w.WriteUInt16((ushort)requestIndices.Length);       
        w.WriteBytes(requestIndices);                       
        return w.ToArray();
    }

 /// <summary>
 /// Builds the 2659 AnsBuyPrivateShopItem refusal body: <c>[01 02][u16 code]</c>. The client shows the matching
 /// message and reads nothing after the code.
 /// </summary>
    internal static byte[] BuildBuyRefusal2659(ushort code)
    {
        if (code == 0) throw new ArgumentOutOfRangeException(nameof(code), "A refusal carries a non-zero result code.");
        using var w = new PacketWriter(3 + BuyRefusalBlobBytes);
        w.WriteCompactLength(BuyRefusalBlobBytes);
        w.WriteUInt16(code);                     
        return w.ToArray();
    }
}
