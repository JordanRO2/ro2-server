using Microsoft.Extensions.Logging;
using RO2.Economy.Application;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Host implementation of <see cref="IAuctionLog"/> over the Zone logger. It owns the log levels and the message
/// templates; the application layer only raises the events, so it stays free of a logging-framework dependency.
/// </summary>
/// <param name="log">The Zone logger the auction flows write through.</param>
public sealed class AuctionLogAdapter(ILogger log) : IAuctionLog
{
    private readonly ILogger _log = log;

    /// <inheritdoc />
    public void BidPlaced(ulong articleUid, string bidderName, long bid, long previousPrice, uint previousBidder, bool bought)
        => _log.LogInformation("Auction {Uid}: {Bidder} bid {Bid} (was {Previous} by char {PreviousBidder}){Bought}",
            articleUid, bidderName, bid, previousPrice, previousBidder,
            bought ? ", reaching the instant price: sold" : string.Empty);

    /// <inheritdoc />
    public void BidRefused(ulong articleUid, ulong bid, string bidderName, ushort code, string why)
        => _log.LogInformation("Auction {Uid} bid {Bid} by {Bidder} refused with {Code}: {Why}",
            articleUid, bid, bidderName, code, why);

    /// <inheritdoc />
    public void Bought(ulong articleUid, string buyerName, long price, string sellerName, long proceeds)
        => _log.LogInformation("Auction {Uid} bought by {Buyer} for {Price}; seller {Seller} receives {Proceeds}",
            articleUid, buyerName, price, sellerName, proceeds);

    /// <inheritdoc />
    public void BuyRefused(ulong articleUid, string buyerName, string why)
        => _log.LogInformation("Auction {Uid} buy by {Buyer} refused: {Why}", articleUid, buyerName, why);

    /// <inheritdoc />
    public void BuyPriceMismatch(ulong articleUid, string buyerName, ulong offered, long price)
        => _log.LogWarning("Auction {Uid}: {Buyer} offered {Offer}, instant price is {Price}; charging the stored price",
            articleUid, buyerName, offered, price);

    /// <inheritdoc />
    public void Cancelled(ulong articleUid, string sellerName, uint itemId, int count)
        => _log.LogInformation("Auction {Uid} cancelled by {Seller}; item {ItemId} x{Count} mailed back",
            articleUid, sellerName, itemId, count);

    /// <inheritdoc />
    public void AcceptRefused(ulong articleUid, string sellerName, string why)
        => _log.LogInformation("Auction {Uid}: sell at bidding price by {Seller} refused: {Why}", articleUid, sellerName, why);

    /// <inheritdoc />
    public void Awarded(ulong articleUid, string bidderName, long bid, string sellerName, long proceeds)
        => _log.LogInformation("Auction {Uid} goes to top bidder {Bidder} for {Bid}; seller {Seller} receives {Proceeds}",
            articleUid, bidderName, bid, sellerName, proceeds);

    /// <inheritdoc />
    public void RefundMailFailed(ulong articleUid, long bid, uint bidderCharId, Exception ex)
        => _log.LogError(ex, "Auction {Uid}: refund of {Bid} to char {Bidder} could not be mailed",
            articleUid, bid, bidderCharId);

    /// <inheritdoc />
    public void ExpirySettleFailed(ulong articleUid, uint itemId, int count, uint sellerCharId, uint topBidderCharId, Exception ex)
        => _log.LogError(ex, "Auction {Uid} expired but its item {ItemId} x{Count} (seller {Seller}, top bidder {Bidder}) could not be mailed",
            articleUid, itemId, count, sellerCharId, topBidderCharId);
}
