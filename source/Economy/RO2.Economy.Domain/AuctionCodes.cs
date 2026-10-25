namespace RO2.Economy.Domain;

/// <summary>
/// The auction protocol result and notice codes the economy flows produce. They are the single source: the host
/// wire layer (AuctionPackets) aliases these, so the application flows and the packet builders agree by value.
/// </summary>
public static class AuctionCodes
{
    /// <summary>The request succeeded.</summary>
    public const ushort ResultSuccess = 0;
    /// <summary>The article is no longer on sale (sold, expired or cancelled).</summary>
    public const ushort ResultArticleGone = 16308;
    /// <summary>The wallet is below the required amount.</summary>
    public const ushort ResultNotEnoughMoney = 16302;
    /// <summary>The bid could not be placed (e.g. the per-character top-bid cap).</summary>
    public const ushort ResultBidFailed = 16306;
    /// <summary>The bid request did not match the article, or is otherwise not allowed.</summary>
    public const ushort ResultBidItemMismatch = 16313;
    /// <summary>Storing the listing failed internally.</summary>
    public const ushort ResultSellInternalError = 16301;
    /// <summary>The instant price is below the start price.</summary>
    public const ushort ResultSellBuyoutBelowStart = 16303;
    /// <summary>The item to list is gone or does not match the request.</summary>
    public const ushort ResultSellItemMismatch = 16304;
    /// <summary>The seller is at the live-listing cap.</summary>
    public const ushort ResultSellQuantityLimit = 16307;

    /// <summary>2693 notice to the seller: the article sold (amount = proceeds).</summary>
    public const uint NotifySold = 1;
    /// <summary>2693 notice to the buyer: bought.</summary>
    public const uint NotifyBought = 2;
    /// <summary>2693 notice to the canceller.</summary>
    public const uint NotifyCancelled = 7;
    /// <summary>2693 notice to the seller: a bid won the article.</summary>
    public const uint NotifyBidSold = 3;
    /// <summary>2693 notice to the bidder: won the article.</summary>
    public const uint NotifyBidWon = 4;
    /// <summary>2693 notice to the bidder: outbid, bid returned.</summary>
    public const uint NotifyOutbid = 5;
    /// <summary>2693 notice to the top bidder: someone bought it at the instant price, bid returned.</summary>
    public const uint NotifyInstantBoughtOverBid = 6;
    /// <summary>2693 notice to the top bidder: the seller cancelled, bid returned.</summary>
    public const uint NotifyCancelledWithBid = 8;
}
