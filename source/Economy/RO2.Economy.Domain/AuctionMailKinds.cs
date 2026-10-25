namespace RO2.Economy.Domain;

/// <summary>
/// The mail kinds the auction settlement produces, each a mail-message kind. They are the single source: the host
/// aliases them (AuctionMailKind for the sale/cancel/expiry kinds, AuctionBidMailKind for the bid kinds), so the
/// settlement policy and the mail store agree by value. The client renders each row's text from its kind.
/// </summary>
public static class AuctionMailKinds
{
    /// <summary>Unsold item returned to its seller when the listing expires.</summary>
    public const byte Expired = 2;

    /// <summary>Item of a cancelled listing returned to its seller.</summary>
    public const byte Cancelled = 7;

    /// <summary>Sale proceeds paid to the seller, as zeny.</summary>
    public const byte Proceeds = 10;

    /// <summary>Purchased item delivered to the buyer.</summary>
    public const byte Purchased = 11;

    /// <summary>The seller's zeny from an article that went to its top bidder.</summary>
    public const byte BidProceeds = 1;

    /// <summary>The item, sent to the bidder who won it.</summary>
    public const byte BidWon = 4;

    /// <summary>The zeny of a displaced bid, returned to its bidder when outbid or when the article is bought at the instant price.</summary>
    public const byte OutbidRefund = 6;

    /// <summary>The top bid's zeny, returned to its bidder when the seller cancels the article.</summary>
    public const byte CancelRefund = 8;
}
