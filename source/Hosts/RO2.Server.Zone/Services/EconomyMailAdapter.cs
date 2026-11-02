using RO2.BuildingBlocks;
using RO2.Mails.Application;
using RO2.Economy.Application;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Host implementation of <see cref="IEconomyMailPort"/>: maps the payout mails (empty sender/title, the kind, the item
/// template at rec+58 and the article price kept on every kind) onto <see cref="ISendMail"/>, and forwards the
/// online-notify to the mail service.
/// </summary>
public sealed class EconomyMailAdapter(ISendMail send, MailService mailService) : IEconomyMailPort
{
    private readonly ISendMail _send = send;
    private readonly MailService _mailService = mailService;

    /// <inheritdoc />
    public Task SendAuctionMailAsync(uint receiverCharId, string receiverName, byte kind, long auctionPrice,
        long gold, ItemStackSnapshot? item, uint kindItemId, DateTime sentAtUtc)
        => _send.SendSystemMailAsync(new MailDraft
        {
            ReceiverCharId = receiverCharId,
            ReceiverName = receiverName,
            Gold = Math.Max(0, gold),
            SentAt = sentAtUtc,
            Kind = kind,
            KindItemId = kindItemId,
            AuctionPrice = Math.Max(0, auctionPrice),
            Item = item is null ? null : item with { Count = item.Count > 0 ? item.Count : 1 },
        });

    /// <inheritdoc />
    public Task NotifyNewMailAsync(uint charId) => _mailService.NotifyNewMailAsync(charId);
}
