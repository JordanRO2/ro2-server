using RO2.Application.Interfaces;
using RO2.BuildingBlocks;
using RO2.Domain.Entities;
using RO2.Economy.Application;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Host implementation of <see cref="IEconomyMailPort"/>: it builds the <see cref="MailMessage"/> the payout mails
/// used to build inline (empty sender/title, the kind, the item template at rec+58 and the article price kept on
/// every kind) and stores it, and forwards the online-notify to the mail service.
/// </summary>
public sealed class EconomyMailAdapter(IMailRepository mails, MailService mailService) : IEconomyMailPort
{
    private readonly IMailRepository _mails = mails;
    private readonly MailService _mailService = mailService;

    /// <inheritdoc />
    public async Task SendAuctionMailAsync(uint receiverCharId, string receiverName, byte kind, long auctionPrice,
        long gold, ItemStackSnapshot? item, uint kindItemId, DateTime sentAtUtc)
    {
        var mail = new MailMessage
        {
            ReceiverCharId = receiverCharId,
            ReceiverName = receiverName,
            Gold = Math.Max(0, gold),
            IsRead = false,
            SentAt = sentAtUtc,
            Kind = kind,
            KindItemId = kindItemId,
            AuctionPrice = Math.Max(0, auctionPrice),
        };
        if (item is { } i)
        {
            mail.AttachedItemUid = i.Id;
            mail.AttachedItemId = i.ItemId;
            mail.AttachedItemCount = i.Count > 0 ? i.Count : 1;
            mail.AttachedItemRefine = i.Refine;
            mail.AttachedItemDurability = i.Durability;
            mail.AttachedItemCardsJson = i.CardsJson;
            mail.AttachedItemElement = i.Element;
            mail.AttachedItemDyeColor = i.DyeColor;
            mail.AttachedItemSocketPunchersJson = i.SocketPunchersJson;
            mail.AttachedItemDyeColor2 = i.DyeColor2;
            mail.AttachedItemDyeColor3 = i.DyeColor3;
            mail.AttachedItemFxEnchantIndex = i.FxEnchantIndex;
            mail.AttachedItemEnchantReinforceId = i.EnchantReinforceId;
            mail.AttachedItemEnchantSeconds = i.EnchantSeconds;
            mail.AttachedItemBound = i.Bound;
            mail.AttachedItemAbilityValue = i.AbilityValue;
        }
        await _mails.AddAsync(mail);
    }

    /// <inheritdoc />
    public Task NotifyNewMailAsync(uint charId) => _mailService.NotifyNewMailAsync(charId);
}
