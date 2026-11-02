using RO2.Characters.Domain;
using RO2.GameData;
using RO2.Inventory.Domain;
using RO2.Mails.Domain;

namespace RO2.Mails.Application;

/// <summary>
/// What a step of the 1393 send decided. The host maps each verdict to its 2445 result code (<c>SystemError</c> 16201,
/// <c>NotEnoughZeny</c> 16203, <c>ItemEmpty</c> and <c>ItemNotTradeable</c> 16208, <c>RecipientNotFound</c> 16202,
/// <c>MailboxFull</c> 16204) and refunds the up-front charge on every refusal.
/// </summary>
public enum MailSendVerdict
{
    /// <summary>The step passes; the host goes on to the next one.</summary>
    Accepted,

    /// <summary>Refused as a system error: the attached zeny is over the cap, or the recipient name is blank.</summary>
    SystemError,

    /// <summary>Refused: the total charge (zeny plus postage) overflows a long, so no wallet can pay it.</summary>
    NotEnoughZeny,

    /// <summary>Refused: the attachment is not an item of the sender's bag (unknown uid, someone else's, or not in a bag).</summary>
    ItemEmpty,

    /// <summary>Refused: the attachment is untradeable or bound. Same wire code as <see cref="ItemEmpty"/>; kept apart so the host logs it.</summary>
    ItemNotTradeable,

    /// <summary>Refused: no live character carries the recipient name.</summary>
    RecipientNotFound,

    /// <summary>Refused: the recipient already holds the live-mail limit.</summary>
    MailboxFull,
}

/// <summary>The charge a send costs: the verdict and, when accepted, the zeny plus postage the host debits up front.</summary>
public readonly record struct MailSendCharge(MailSendVerdict Verdict, long Charge);

/// <summary>The resolved recipient of a send, or the refusal.</summary>
public readonly record struct MailSendRecipient(MailSendVerdict Verdict, Character? Recipient);

/// <summary>Whether the recipient's mailbox has room, with the live count it was judged on (for the host's log).</summary>
public readonly record struct MailSendMailbox(MailSendVerdict Verdict, long Held);

/// <summary>
/// The decisions of a 1393 mail send, in the order the host takes them: the zeny cap and total charge, the attachment
/// checks, the recipient lookup, the mailbox-full test and the mail to persist. Pure over snapshots and the character
/// and mail ports: no session, wallet, wire or game-data store. The host keeps the wallet debit (and the credit-back on
/// every refusal), the recipient gate, the take of the item, the store, the 2449 notify, the Khara held-item report
/// (2577, BEFORE the 2445) and the 2445 itself, in that order.
/// </summary>
public sealed class MailSendUseCase(ICharacterRepository characters, IMailRepository mails)
{
    /// <summary>InventoryItem.StorageType 0: the item sits in a bag (the only place an attachment may come from).</summary>
    private const byte StorageBag = 0;

    /// <summary>1393 item uid meaning "no attachment".</summary>
    private const ulong NoAttachment = 0;

    /// <summary>Sender name stored on a mail whose sender has no name.</summary>
    private const string UnknownSenderName = "Unknown";

    /// <summary>
    /// The send charge for <paramref name="gold"/> attached zeny: refused as a system error when it is over
    /// <paramref name="zenyCap"/> (Config_Def 92; not as 16209), as not enough zeny when zeny plus postage overflows.
    /// Nothing is debited here; the host debits <see cref="MailSendCharge.Charge"/> atomically.
    /// </summary>
    public static MailSendCharge DecideCharge(long gold, long zenyCap)
    {
        if (!MailRules.IsAttachableZeny(gold, zenyCap)) return new(MailSendVerdict.SystemError, 0);
        return MailRules.TryTotalCharge(gold, out long total)
            ? new(MailSendVerdict.Accepted, total)
            : new(MailSendVerdict.NotEnoughZeny, 0);
    }

    /// <summary>
    /// Whether the attachment may be mailed. <paramref name="attached"/> is the row the host read for
    /// <paramref name="itemUid"/> (null when none); <paramref name="template"/> is its item template, or null when the
    /// data does not know it (an unknown template is not refused).
    /// </summary>
    public static MailSendVerdict DecideAttachment(ulong itemUid, InventoryItem? attached, uint senderCharId, ItemData? template)
    {
        if (itemUid == NoAttachment) return MailSendVerdict.Accepted;
        if (attached == null || attached.CharacterId != senderCharId || attached.StorageType != StorageBag)
            return MailSendVerdict.ItemEmpty;
        return RefusesAttachment(template, attached) ? MailSendVerdict.ItemNotTradeable : MailSendVerdict.Accepted;
    }

    /// <summary>True when the attached item must not be mailed: its template is known and it is untradeable or bound.</summary>
    public static bool RefusesAttachment(ItemData? template, InventoryItem attached)
        => template != null && ItemTradeRules.RefusedAsNotTradeable(template.Is_Trade, template.Bind_Type, attached.StorageType, attached.Bound);

    /// <summary>
    /// Resolves the recipient by name: exact match first, then case-insensitive. A blank name is a system error
    /// (the host answers 16201); an unknown one is not found. A name with a trailing space matches neither.
    /// </summary>
    public async Task<MailSendRecipient> ResolveRecipientAsync(string? recipientName)
    {
        if (string.IsNullOrWhiteSpace(recipientName)) return new(MailSendVerdict.SystemError, null);
        var recipient = await characters.FindByExactNameAsync(recipientName)
                        ?? await characters.FindByNameIgnoreCaseAsync(recipientName);
        return recipient == null ? new(MailSendVerdict.RecipientNotFound, null) : new(MailSendVerdict.Accepted, recipient);
    }

    /// <summary>
    /// Whether <paramref name="recipient"/>'s box has room: counts every mail younger than the lifetime, read or not.
    /// The host holds the recipient's gate from this call to the store so two sends cannot both pass at limit minus one.
    /// </summary>
    public async Task<MailSendMailbox> DecideMailboxAsync(Character recipient, DateTime nowUtc)
    {
        long held = await mails.CountLiveInboxAsync(recipient.Id, MailRules.LiveCutoff(nowUtc));
        return new(MailRules.IsMailboxFull(held) ? MailSendVerdict.MailboxFull : MailSendVerdict.Accepted, held);
    }

    /// <summary>
    /// The mail to persist: the draft for <paramref name="recipient"/> carrying the zeny, the text and, when the host took
    /// an item off the sender (<paramref name="taken"/> x<paramref name="takenCount"/>), a full snapshot of it.
    /// </summary>
    public static MailMessage ComposeMail(Character recipient, uint senderCharId, string? senderName, string? title, string? body,
        long gold, DateTime sentAtUtc, InventoryItem? taken, int takenCount)
    {
        var mail = new MailMessage
        {
            ReceiverCharId = recipient.Id,
            ReceiverName = recipient.Name,
            SenderCharId = senderCharId,
            SenderName = string.IsNullOrEmpty(senderName) ? UnknownSenderName : senderName,
            Title = title ?? string.Empty,
            Body = body ?? string.Empty, // 1393 request: UTF-16 body text at offset +113
            Gold = gold,                 // 1393 request: attached zeny, u64 at offset +92; checked against the cap and the wallet first
            IsRead = false,
            SentAt = sentAtUtc,
        };

        if (taken != null)
        {
            mail.AttachedItemUid = taken.Id;
            mail.AttachedItemId = taken.ItemId;
            mail.AttachedItemCount = takenCount;
            mail.AttachedItemRefine = taken.Refine;
            mail.AttachedItemDurability = taken.Durability;
            mail.AttachedItemCardsJson = taken.CardsJson;
            mail.AttachedItemElement = taken.Element; // else the claim re-creates it neutral
            mail.AttachedItemDyeColor = taken.DyeColor;
            mail.AttachedItemSocketPunchersJson = taken.SocketPunchersJson;
            mail.AttachedItemDyeColor2 = taken.DyeColor2;
            mail.AttachedItemDyeColor3 = taken.DyeColor3;
            mail.AttachedItemFxEnchantIndex = taken.FxEnchantIndex;
            mail.AttachedItemEnchantReinforceId = taken.EnchantReinforceId;
            mail.AttachedItemEnchantSeconds = taken.EnchantSeconds;
            mail.AttachedItemBound = taken.Bound;
            mail.AttachedItemAbilityValue = taken.AbilityValue;
        }
        return mail;
    }
}
