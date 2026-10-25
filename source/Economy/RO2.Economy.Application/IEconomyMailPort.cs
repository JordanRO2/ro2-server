using RO2.BuildingBlocks;

namespace RO2.Economy.Application;

/// <summary>
/// The mail deliveries the economy payout flows need (auction proceeds, won items, outbid/cancel refunds). The
/// host implements it over the mail store and the online-notify; the economy application depends only on this
/// contract. Auction mails carry an empty sender and title — the client renders the text from the kind and the
/// item template — and keep the article's instant price on every kind.
/// </summary>
public interface IEconomyMailPort
{
    /// <summary>
    /// Stores a payout mail for <paramref name="receiverCharId"/> carrying <paramref name="item"/> (when non-null)
    /// and/or <paramref name="gold"/> zeny, tagged with the list-row <paramref name="kind"/>, the
    /// <paramref name="kindItemId"/> template and the <paramref name="auctionPrice"/>.
    /// </summary>
    Task SendAuctionMailAsync(uint receiverCharId, string receiverName, byte kind, long auctionPrice, long gold,
        ItemStackSnapshot? item, uint kindItemId, DateTime sentAtUtc);

    /// <summary>Notifies a character of new mail (2449) if they are online.</summary>
    Task NotifyNewMailAsync(uint charId);
}
