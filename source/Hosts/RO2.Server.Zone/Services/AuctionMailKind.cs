using RO2.Domain.Entities;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Mail kinds used for auction results. Each value is a <see cref="MailMessage.Kind"/>.
/// </summary>
public static class AuctionMailKind
{
 /// <summary>Unsold item returned to its seller when the listing expires.</summary>
    public const byte Expired = 2;

 /// <summary>Item of a cancelled listing returned to its seller.</summary>
    public const byte Cancelled = 7;

 /// <summary>Sale proceeds paid to the seller, as zeny.</summary>
    public const byte Proceeds = 10;

 /// <summary>Purchased item delivered to the buyer.</summary>
    public const byte Purchased = 11;

 /// <summary>True for the auction mail kinds, including the bid kinds of <see cref="AuctionBidMailKind"/>.</summary>
 /// <remarks>
 /// Each mail carries a single attachment: the item for kinds 2, 7, 11 and 4, zeny for kinds 10, 1, 6 and 8. A mail is removed once its attachment is claimed (see <see cref="MailService.ClaimRemovesMail"/>). Item attachments stay in the auction container until claimed.
 /// </remarks>
 /// <param name="kind">Mail kind to test.</param>
    public static bool IsAuction(byte kind) => kind is Expired or Cancelled or Proceeds or Purchased
        or AuctionBidMailKind.BidProceeds or AuctionBidMailKind.BidWon
        or AuctionBidMailKind.OutbidRefund or AuctionBidMailKind.CancelRefund;
}
