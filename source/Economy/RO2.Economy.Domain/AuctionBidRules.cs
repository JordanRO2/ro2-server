namespace RO2.Economy.Domain;

/// <summary>
/// The rules a bid must pass, as pure domain logic. The host wire layer (AuctionPackets/AuctionService) parses the
/// request and builds the reply; the decision — whether a bid is accepted and, if not, with which code — is here,
/// so the application flow and the packet layer agree by value.
/// </summary>
public static class AuctionBidRules
{
    /// <summary>
    /// Maximum number of articles one character may be top bidder on. The client refuses a bid at 100 top bids only
    /// once its own list reaches the limit, and the server never reports a non-zero count, so the server enforces it.
    /// </summary>
    public const int MaxTopBids = 100;

    /// <summary>
    /// Checks a bid against the rules, in order (item identity, own article, already top bidder, below the minimum
    /// or above the instant price, bid cap, wallet). Returns <see cref="AuctionCodes.ResultSuccess"/> when it passes,
    /// otherwise the refusal code. A bid equal to the instant price is valid at any minimum and completes the purchase.
    /// </summary>
    /// <param name="article">Article being bid on.</param>
    /// <param name="bidderCharId">Bidder's character id.</param>
    /// <param name="reqItemUid">Item uid named by the request.</param>
    /// <param name="reqItemId">Item template id named by the request.</param>
    /// <param name="reqBid">Bid amount named by the request.</param>
    /// <param name="wallet">Bidder's zeny.</param>
    /// <param name="activeTopBids">How many articles the bidder is already top bidder on.</param>
    public static ushort Check(AuctionArticle article, uint bidderCharId, ulong reqItemUid, uint reqItemId, ulong reqBid,
        long wallet, int activeTopBids)
    {
        if (reqItemUid != article.ItemUid || reqItemId != article.ItemId) return AuctionCodes.ResultBidItemMismatch;
        if (article.SellerCharId == bidderCharId) return AuctionCodes.ResultBidItemMismatch;
        if (article.TopBidderCharId == bidderCharId) return AuctionCodes.ResultBidItemMismatch;
        if (reqBid == 0 || reqBid > long.MaxValue) return AuctionCodes.ResultBidItemMismatch;
        long bid = (long)reqBid;
        bool instant = article.BuyoutPrice > 0 && bid == article.BuyoutPrice;
        if (article.BuyoutPrice > 0 && bid > article.BuyoutPrice) return AuctionCodes.ResultBidItemMismatch;
        if (!instant && bid < AuctionPricing.MinimumBid(AuctionPricing.CurrentPrice(article))) return AuctionCodes.ResultBidItemMismatch;
        if (activeTopBids >= MaxTopBids) return AuctionCodes.ResultBidFailed;
        if (wallet < bid) return AuctionCodes.ResultNotEnoughMoney;
        return AuctionCodes.ResultSuccess;
    }
}
