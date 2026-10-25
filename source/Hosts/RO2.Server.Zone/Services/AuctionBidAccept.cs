using RO2.Economy.Domain;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Network.Packets;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

public static partial class AuctionPackets
{
 /// <summary>Message tag of the 1404 ReqAuctionSellTenderPrice request (0xE368 = 58216).</summary>
    public const ushort MsgTagReqSellTenderPrice = 0xE368;

 /// <summary>Blob length (the leading u16 innerSize) of the 1404 request: 24 bytes.</summary>
    private const int SellTenderPriceBlobLength = 24;

 /// <summary>Initial buffer capacity for the 2459 answer, whose compact-length body is 26 bytes.</summary>
    private const int SellTenderPriceAnswerCapacity = 32;

 /// <summary>
 /// Message tag of the 2459 OnAuctionSellTenderPrice answer (0xE369 = 58217).
 /// </summary>
 /// <remarks>
 /// Follows the request tag plus one, the same rule as the other request and answer pairs of this family.
 /// </remarks>
    public const ushort MsgTagSellTenderPrice = 0xE369;

 /// <summary>
 /// Parses a 1404 ReqAuctionSellTenderPrice payload.
 /// </summary>
 /// <remarks>
 /// Layout: [u16 24][u16 58216][u32 requester handle @4][u64 article uid @8][u64 item uid @16]. Same shape as the cancel request (<see cref="TryParseCancel"/>).
 /// </remarks>
 /// <param name="payload">Request payload.</param>
 /// <param name="articleUid">Uid of the auction article.</param>
 /// <param name="itemUid">Uid of the item being sold.</param>
    public static bool TryParseSellTenderPrice(ReadOnlySpan<byte> payload, out ulong articleUid, out ulong itemUid)
    {
        articleUid = itemUid = 0;
        if (!MailPackets.TryGetBlob(payload, out var b) || b.Length < SellTenderPriceBlobLength) return false;
        if (BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(BlobTagOffset)) != MsgTagReqSellTenderPrice) return false;
        articleUid = BinaryPrimitives.ReadUInt64LittleEndian(b.Slice(ArticleUidOffset));
        itemUid = BinaryPrimitives.ReadUInt64LittleEndian(b.Slice(ItemUidOffset));
        return true;
    }

 /// <summary>
 /// Builds the 2459 OnAuctionSellTenderPrice answer to a tender price request.
 /// </summary>
 /// <remarks>
 /// Body is 26 bytes: [u16 26][u16 58217][u32 handle @4][u16 result @8][u64 article uid @10][u64 item uid @18]. Uses the same shape as 2460 (<see cref="BuildSellCancel2460"/>).
 /// </remarks>
 /// <param name="handle">Handle of the requesting player.</param>
 /// <param name="result">Result code.</param>
 /// <param name="articleUid">Uid of the auction article.</param>
 /// <param name="itemUid">Uid of the item being sold.</param>
    public static byte[] BuildSellTenderPrice2459(uint handle, ushort result, ulong articleUid, ulong itemUid)
    {
        using var pw = new PacketWriter(SellTenderPriceAnswerCapacity);
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagSellTenderPrice);
            p.WriteUInt32(handle);     // blob+4
            p.WriteUInt16(result);     // blob+8
            p.WriteUInt64(articleUid); // blob+10
            p.WriteUInt64(itemUid);    // blob+18
        });
        return pw.ToArray();
    }
}

public sealed partial class AuctionService
{
 /// <summary>
 /// Sells an article to its top bidder at the top bid.
 /// </summary>
 /// <remarks>
 /// Only the seller's own article can be sold: it must be active, unexpired, match the requested item, and have a top bidder. The close is atomic on the bid (see <see cref="IAuctionRepository.TryAcceptTopBidAsync"/>), so a bid placed in the meantime makes the sale fail rather than complete at the old price. Every refusal returns null.
 /// </remarks>
 /// <param name="seller">Seller's player state.</param>
 /// <param name="articleUid">Uid of the article.</param>
 /// <param name="itemUid">Uid of the item being sold.</param>
 /// <returns>The sold article, or null when the sale is refused.</returns>
    public Task<AuctionArticle?> AcceptTopBidAsync(PlayerState seller, ulong articleUid, ulong itemUid)
        => _app.AcceptTopBidAsync(seller.CharacterId, seller.Name, articleUid, itemUid);

}
