using RO2.BuildingBlocks;
using RO2.Mails.Domain;

namespace RO2.Mails.Application;

/// <summary>
/// What the other bounded contexts (Dungeon Finder and Illusion Pyramid rewards, the economy payouts) call to put a
/// system mail in a character's mailbox, without building a <see cref="MailMessage"/> or reaching into the mail store
/// themselves. The player-to-player send is not here: it has its own use case.
/// </summary>
public interface ISendMail
{
    /// <summary>
    /// Composes the mail from <paramref name="draft"/> and stores it. Returns the stored mail. A failure propagates to the
    /// caller, which decides whether to swallow it. Sends no 2449 unless <see cref="MailDraft.Notify"/> is set.
    /// </summary>
    Task<MailMessage> SendSystemMailAsync(MailDraft draft);
}

/// <summary>A system mail to send: the receiver, the text ids, the list-row kind and the optional attachments.</summary>
public sealed record MailDraft
{
    /// <summary>The receiving character's id.</summary>
    public required uint ReceiverCharId { get; init; }

    /// <summary>The receiving character's name.</summary>
    public required string ReceiverName { get; init; }

    /// <summary>The 2444 list-row kind.</summary>
    public required byte Kind { get; init; }

    /// <summary>When the mail was sent (UTC).</summary>
    public required DateTime SentAt { get; init; }

    /// <summary>The sender text (a client string id for reward mails, empty for auction mails).</summary>
    public string SenderName { get; init; } = string.Empty;

    /// <summary>The title text (a client string id for reward mails, empty for auction mails).</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>The body text (a client string id for reward mails, empty for auction mails).</summary>
    public string Body { get; init; } = string.Empty;

    /// <summary>The item template the mail is about (2444 rec+58).</summary>
    public uint KindItemId { get; init; }

    /// <summary>The article price an auction mail carries; 0 otherwise.</summary>
    public long AuctionPrice { get; init; }

    /// <summary>Zeny attached.</summary>
    public long Gold { get; init; }

    /// <summary>The attached item, or null for none.</summary>
    public ItemStackSnapshot? Item { get; init; }

    /// <summary>True to also send the online 2449 new-mail notice. Off: system and reward mails send none.</summary>
    public bool Notify { get; init; }
}
