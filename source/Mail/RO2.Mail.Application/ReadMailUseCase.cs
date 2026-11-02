using RO2.Mails.Domain;

namespace RO2.Mails.Application;

/// <summary>
/// The store side of a 1397 read: fetch the reader's mail and mark it read. Null for an unknown uid or another player's
/// mail (the host answers 16203). An expired mail is still readable. The write on the unread-to-read transition is the
/// whole-document <see cref="IMailRepository.ReplaceAsync"/>, as before. The 2446 body stays in the Zone host.
/// </summary>
public sealed class ReadMailUseCase(IMailRepository mails)
{
    /// <summary>Reads (and marks read) the reader's mail, or null when there is none.</summary>
    public async Task<MailMessage?> ReadAsync(uint charId, ulong uid)
    {
        var mail = await mails.GetByUidAsync(charId, uid);
        if (mail == null) return null;
        if (mail.MarkRead()) await mails.ReplaceAsync(mail);
        return mail;
    }
}
