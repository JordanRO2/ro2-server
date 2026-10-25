namespace RO2.Economy.Application;

/// <summary>
/// The operational log of the auction flows, as events. The application raises an intention ("a bid was placed",
/// "a refund could not be mailed"); the host implements it over its logger and owns the levels and templates. This
/// keeps the application layer free of a logging-framework dependency, like the mail and notice ports.
/// </summary>
public interface IAuctionLog
{
    /// <summary>A bid was accepted on an article (and, when <paramref name="bought"/>, reached the instant price and sold).</summary>
    void BidPlaced(ulong articleUid, string bidderName, long bid, long previousPrice, uint previousBidder, bool bought);

    /// <summary>A bid was refused with a result code and a human-readable reason.</summary>
    void BidRefused(ulong articleUid, ulong bid, string bidderName, ushort code, string why);

    /// <summary>An article was bought at its instant price; the seller receives the proceeds.</summary>
    void Bought(ulong articleUid, string buyerName, long price, string sellerName, long proceeds);

    /// <summary>An instant buy was refused for a human-readable reason.</summary>
    void BuyRefused(ulong articleUid, string buyerName, string why);

    /// <summary>A buyer offered a price that did not match the article's stored instant price; the stored price is charged.</summary>
    void BuyPriceMismatch(ulong articleUid, string buyerName, ulong offered, long price);

    /// <summary>A seller cancelled an article; its item is mailed back.</summary>
    void Cancelled(ulong articleUid, string sellerName, uint itemId, int count);

    /// <summary>A seller's accept-at-bidding-price was refused for a human-readable reason.</summary>
    void AcceptRefused(ulong articleUid, string sellerName, string why);

    /// <summary>An article went to its top bidder; the seller receives the proceeds.</summary>
    void Awarded(ulong articleUid, string bidderName, long bid, string sellerName, long proceeds);

    /// <summary>A displaced bid's refund mail could not be stored; the failure is swallowed and recorded.</summary>
    void RefundMailFailed(ulong articleUid, long bid, uint bidderCharId, Exception ex);

    /// <summary>An expired article could not be mailed during the sweep; it is not re-opened and is recorded for recovery.</summary>
    void ExpirySettleFailed(ulong articleUid, uint itemId, int count, uint sellerCharId, uint topBidderCharId, Exception ex);
}
