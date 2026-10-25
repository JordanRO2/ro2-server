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

/// <summary>
/// <see cref="RO2.Domain.Entities.MailMessage.Kind"/> values of the mails produced by a bid, next to the auction kinds in <see cref="AuctionMailKind"/>.
/// </summary>
/// <remarks>
/// <para>
/// The kinds are listed with the text they carry:
/// </para>
/// <list type="bullet">
/// <item>BidProceeds (1): the seller's proceeds from a bid sale.</item>
/// <item>BidWon (4): the item, sent to the winning bidder.</item>
/// <item>OutbidRefund (6): zeny returned to a bidder who was outbid.</item>
/// <item>CancelRefund (8): zeny returned to the top bidder of a cancelled article.</item>
/// </list>
/// <para>
/// Kinds 3, 5 and 9 carry no text. The notice types below pair with these kinds.
/// </para>
/// </remarks>
public static class AuctionBidMailKind
{
 /// <summary>The seller's zeny from an article that went to its top bidder.</summary>
    public const byte BidProceeds = AuctionMailKinds.BidProceeds;

 /// <summary>The item, sent to the bidder who won it.</summary>
    public const byte BidWon = AuctionMailKinds.BidWon;

 /// <summary>The zeny of a displaced bid, returned to its bidder when outbid or when the article is bought at the instant price.</summary>
    public const byte OutbidRefund = AuctionMailKinds.OutbidRefund;

 /// <summary>The top bid's zeny, returned to its bidder when the seller cancels the article.</summary>
    public const byte CancelRefund = AuctionMailKinds.CancelRefund;
}

public static partial class AuctionPackets
{
 /// <summary>Message tag of the 1407 ReqAuctionTenderJoin request: 58222. The blob starts with [u16 36][u16 58222].</summary>
    public const ushort MsgTagReqTenderJoin = 0xE36E;

 /// <summary>Message tag of the 2462 OnAuctionTenderJoin answer: 58223, the request tag plus one.</summary>
    public const ushort MsgTagTenderJoin = 0xE36F;

 /// <summary>Length in bytes of the 1407 ReqAuctionTenderJoin blob.</summary>
    private const int TenderJoinRequestLength = 36;

    // Byte offsets of the 1407 blob fields (see TryParseTenderJoin).
    private const int TenderJoinMsgTagOffset = 2;
    private const int TenderJoinArticleUidOffset = 8;
    private const int TenderJoinItemUidOffset = 16;
    private const int TenderJoinItemIdOffset = 24;
    private const int TenderJoinBidOffset = 28;

 /// <summary>Initial buffer capacity for the 2462 answer, whose blob is 42 bytes.</summary>
    private const int TenderJoinAnswerCapacity = 48;

    
 /// <summary>Result code 16302: not enough zeny in the wallet (client text 2114 "Not enough money in your inventory.").</summary>
    public const ushort ResultNotEnoughMoney = AuctionCodes.ResultNotEnoughMoney;
 /// <summary>Result code 16306: internal error while placing the bid (client text 8609). It is also what a character at the <see cref="MaxTopBids"/> limit gets.</summary>
    public const ushort ResultBidFailed = AuctionCodes.ResultBidFailed;
 /// <summary>Result code 16313: the item information does not match the article (client text 8612).</summary>
    public const ushort ResultBidItemMismatch = AuctionCodes.ResultBidItemMismatch;

    
    
 // Types of the 2693 NfyAuctionSystemMessage notice for bid events; the client prints a chat line per type
 // (see AuctionBidMailKind for the texts).
 /// <summary>Notice type to the seller: the article went to its top bidder (text 8646).</summary>
    public const uint NotifyBidSold = AuctionCodes.NotifyBidSold;
 /// <summary>Notice type to the top bidder: the article is theirs (text 8654).</summary>
    public const uint NotifyBidWon = AuctionCodes.NotifyBidWon;
 /// <summary>Notice type to a bidder who was outbid (text 8656).</summary>
    public const uint NotifyOutbid = AuctionCodes.NotifyOutbid;
 /// <summary>Notice type to the top bidder of an article bought at the instant price by another player (text 8013).</summary>
    public const uint NotifyInstantBoughtOverBid = AuctionCodes.NotifyInstantBoughtOverBid;
 /// <summary>Notice type to the top bidder of an article its seller cancelled (text 8657).</summary>
    public const uint NotifyCancelledWithBid = AuctionCodes.NotifyCancelledWithBid;

 /// <summary>
 /// Maximum number of articles one character may be top bidder on.
 /// </summary>
 /// <remarks>
 /// The client refuses a bid at 100 top bids (text 8632 "Exceeded the item quantity limit a player can bid on.") only when its list count reaches the limit, and the server never reports a non-zero count, so the client does not apply it itself; the server enforces it.
 /// </remarks>
    public const int MaxTopBids = AuctionBidRules.MaxTopBids;

 /// <summary>Parsed 1407 ReqAuctionTenderJoin request.</summary>
    public readonly record struct TenderJoinRequest(ulong ArticleUid, ulong ItemUid, uint ItemId, ulong Bid);

 /// <summary>
 /// Parses a 1407 ReqAuctionTenderJoin payload.
 /// </summary>
 /// <remarks>
 /// Layout: [u16 36][u16 58222][u32 handle @4][u64 article uid @8][u64 item uid @16][u32 item template id @24][u64 bid @28].
 /// </remarks>
 /// <param name="payload">Request payload.</param>
 /// <param name="req">Parsed request.</param>
    public static bool TryParseTenderJoin(ReadOnlySpan<byte> payload, out TenderJoinRequest req)
    {
        req = default;
        if (!MailPackets.TryGetBlob(payload, out var b) || b.Length < TenderJoinRequestLength) return false;
        if (BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(TenderJoinMsgTagOffset)) != MsgTagReqTenderJoin) return false;
        req = new TenderJoinRequest(
            BinaryPrimitives.ReadUInt64LittleEndian(b.Slice(TenderJoinArticleUidOffset)),
            BinaryPrimitives.ReadUInt64LittleEndian(b.Slice(TenderJoinItemUidOffset)),
            BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(TenderJoinItemIdOffset)),
            BinaryPrimitives.ReadUInt64LittleEndian(b.Slice(TenderJoinBidOffset)));
        return true;
    }

 /// <summary>Gets the current price of an article: the top bid once there is one, otherwise the start price.</summary>
    public static long CurrentPrice(AuctionArticle a) => AuctionPricing.CurrentPrice(a);

 /// <summary>The lowest bid accepted on an article with the given current price (current + 10%, at least 1).</summary>
    public static long MinimumBid(long current) => AuctionPricing.MinimumBid(current);

 /// <summary>
 /// Builds the 2462 OnAuctionTenderJoin answer to a bid.
 /// </summary>
 /// <remarks>
 /// Body is 42 bytes: [u16 42][u16 58223][u32 handle @4][u16 result @8][u64 article uid @10][u64 item uid @18][u64 bid @26][u64 bidder wallet @34]. The bidder wallet is the balance after the bid.
 /// </remarks>
 /// <param name="handle">Handle of the bidder.</param>
 /// <param name="result">Result code.</param>
 /// <param name="articleUid">Uid of the article.</param>
 /// <param name="itemUid">Uid of the item.</param>
 /// <param name="bid">Bid amount.</param>
 /// <param name="wallet">Bidder's zeny after the bid.</param>
    public static byte[] BuildTenderJoin2462(uint handle, ushort result, ulong articleUid, ulong itemUid, ulong bid, long wallet)
    {
        using var pw = new PacketWriter(TenderJoinAnswerCapacity);
        pw.WriteCompactBABody(p =>
        {
            p.WriteUInt16(MsgTagTenderJoin);
            p.WriteUInt32(handle);                      // blob+4
            p.WriteUInt16(result);                      // blob+8
            p.WriteUInt64(articleUid);                  // blob+10
            p.WriteUInt64(itemUid);                     // blob+18
            p.WriteUInt64(bid);                         // blob+26
            p.WriteUInt64((ulong)Math.Max(0, wallet));  // blob+34
        });
        return pw.ToArray();
    }
}

public sealed partial class AuctionService
{
 /// <summary>
 /// Checks a bid against the rules the client applies, in order (own article, already top bidder, below the minimum, bid cap, wallet), after the article and item identity the request names. Returns <see cref="AuctionPackets.ResultSuccess"/> when it passes, otherwise the refusal code.
 /// </summary>
 /// <remarks>
 /// Returns 16313 for a request that does not match the article: the bidder's own article, the bidder's own top bid, a bid below the minimum, or a bid above the instant price. Returns 16306 when the bidder is at the bid limit, and 16302 when the wallet cannot cover the bid. A bid equal to the instant price is valid at any minimum, and completes the purchase.
 /// </remarks>
 /// <param name="article">Article being bid on.</param>
 /// <param name="bidderCharId">Bidder's character id.</param>
 /// <param name="req">Bid request.</param>
    public static ushort CheckBid(AuctionArticle article, uint bidderCharId, AuctionPackets.TenderJoinRequest req,
        long wallet, int activeTopBids)
        => AuctionBidRules.Check(article, bidderCharId, req.ItemUid, req.ItemId, req.Bid, wallet, activeTopBids);

 /// <summary>
 /// Places a bid. The zeny is taken from the bidder's wallet only after the article accepts the bid atomically, and the displaced bid is refunded by mail.
 /// </summary>
 /// <remarks>
 /// A bid at the instant price closes the article to the bidder at once. A request for an article that is gone or expired is answered with <see cref="AuctionPackets.ResultArticleGone"/>.
 /// </remarks>
 /// <param name="bidder">Bidder's player state.</param>
 /// <param name="req">Bid request.</param>
 /// <param name="characters">Character repository.</param>
 /// <returns>The 2462 result: <see cref="AuctionPackets.ResultSuccess"/> when the bid was placed or the article was bought, otherwise the refusal code.</returns>
    public async Task<ushort> PlaceBidAsync(PlayerState bidder, AuctionPackets.TenderJoinRequest req, ICharacterRepository characters)
    {
        var now = DateTime.UtcNow;
        var article = await _repo.GetByUidAsync(req.ArticleUid);
        if (article == null || article.State != AuctionArticleState.Active) return Refused(AuctionPackets.ResultArticleGone, "not on sale");
        if (article.ExpiresAt <= now)
        {
            var expired = await _repo.TryExpireAsync(article.Uid, now);
            if (expired != null) await _app.SettleExpiredNowAsync(expired, now);
            return Refused(AuctionPackets.ResultArticleGone, "expired");
        }

        int topBids = await _repo.CountTopBidsAsync(bidder.CharacterId, now);
        ushort code = CheckBid(article, bidder.CharacterId, req, bidder.Gold, topBids);
        if (code != AuctionPackets.ResultSuccess) return Refused(code, $"rule (current {AuctionPackets.CurrentPrice(article)}, instant {article.BuyoutPrice}, wallet {bidder.Gold}, {topBids} top bids)");

        long bid = (long)req.Bid;
        bool buys = article.BuyoutPrice > 0 && bid >= article.BuyoutPrice;
        
        
        if (!bidder.TryDebit(bid)) return Refused(AuctionPackets.ResultNotEnoughMoney, $"wallet {bidder.Gold} below the bid {bid}");
        AuctionArticle? updated;
        try
        {
            updated = buys
                ? await _repo.TryWinAtInstantPriceAsync(article, bidder.CharacterId, bidder.Name, bidder.CharacterUid, bid, now)
                : await _repo.TryPlaceBidAsync(article, bidder.CharacterId, bidder.Name, bidder.CharacterUid, bid, now);
        }
        catch
        {
            bidder.Credit(bid);
            throw;
        }
        if (updated == null)
        {
            bidder.Credit(bid);
            
            
            var again = await _repo.GetByUidAsync(req.ArticleUid);
            bool open = again is { State: AuctionArticleState.Active } && again.ExpiresAt > now;
            return Refused(open ? AuctionPackets.ResultBidItemMismatch : AuctionPackets.ResultArticleGone, "changed meanwhile");
        }

        await characters.UpdateGoldAsync(bidder.CharacterId, bidder.Gold);

        
        if (article.TopBidderCharId != 0)
            await _app.RefundTopBidAsync(article, AuctionBidMailKind.OutbidRefund, AuctionPackets.NotifyOutbid, notifyBidder: true, now);

        if (buys)
        {
            
            
            var receivers = new HashSet<uint>();
            await _app.AwardToTopBidderAsync(updated, now, notifyBidder: false, receivers);
            foreach (var receiver in receivers)
                await _mailPort.NotifyNewMailAsync(receiver);
        }

        _log.LogInformation("Auction {Uid}: {Bidder} bid {Bid} (was {Previous} by char {PreviousBidder}){Bought}",
            updated.Uid, bidder.Name, bid, AuctionPackets.CurrentPrice(article), article.TopBidderCharId,
            buys ? ", reaching the instant price: sold" : string.Empty);
        return AuctionPackets.ResultSuccess;

        ushort Refused(ushort result, string why)
        {
            _log.LogInformation("Auction {Uid} bid {Bid} by {Bidder} refused with {Code}: {Why}",
                req.ArticleUid, req.Bid, bidder.Name, result, why);
            return result;
        }
    }

 /// <summary>Gets the active articles the character is top bidder on, newest first.</summary>
 /// <param name="bidderCharId">Bidder's character id.</param>
    public async Task<List<AuctionArticle>> GetTopBidsAsync(uint bidderCharId) => (await _repo.GetTopBidsAsync(bidderCharId, DateTime.UtcNow)).ToList();

}
