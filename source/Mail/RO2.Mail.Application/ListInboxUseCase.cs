using RO2.Mails.Domain;

namespace RO2.Mails.Application;

/// <summary>
/// The store side of a 1394 list: the reader's inbox (newest first, at most 200) projected onto 2444 rows by
/// <see cref="MailListProjection"/>. The mailbox-NPC gate (a refused one answers the empty list without calling this) and
/// the 146-byte row bytes stay in the Zone host.
/// </summary>
public sealed class ListInboxUseCase(IMailRepository mails)
{
    /// <summary>The reader's inbox as 2444 list rows, newest first.</summary>
    public async Task<IReadOnlyList<MailListRow>> ListAsync(uint charId)
    {
        var inbox = await mails.GetInboxAsync(charId);
        return inbox.Select(MailListProjection.Project).ToList();
    }
}
