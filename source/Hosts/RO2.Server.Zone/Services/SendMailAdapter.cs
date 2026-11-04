using RO2.Mails.Application;
using RO2.Mails.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Host implementation of <see cref="ISendMail"/>: composes the <see cref="MailMessage"/> from the draft and stores it.
/// The online 2449 notice goes out only when the draft asks for it, through <paramref name="notify"/>.
/// </summary>
public sealed class SendMailAdapter(IMailRepository mails, Func<uint, Task>? notify = null) : ISendMail
{
    /// <inheritdoc />
    public async Task<MailMessage> SendSystemMailAsync(MailDraft draft)
    {
        var mail = new MailMessage
        {
            ReceiverCharId = draft.ReceiverCharId,
            ReceiverName = draft.ReceiverName,
            SenderName = draft.SenderName,
            Title = draft.Title,
            Body = draft.Body,
            Gold = draft.Gold,
            IsRead = false,
            SentAt = draft.SentAt,
            Kind = draft.Kind,
            KindItemId = draft.KindItemId,
            AuctionPrice = draft.AuctionPrice,
        };
        if (draft.Item is { } i)
        {
            mail.AttachedItemUid = i.Id;
            mail.AttachedItemId = i.ItemId;
            mail.AttachedItemCount = i.Count;
            mail.AttachedItemRefine = i.Refine;
            mail.AttachedItemDurability = i.Durability;
            mail.AttachedItemCardsJson = i.CardsJson;
            mail.AttachedItemGrindingJson = i.GrindingJson;
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
        await mails.AddAsync(mail);
        if (draft.Notify && notify is not null) await notify(draft.ReceiverCharId);
        return mail;
    }
}
