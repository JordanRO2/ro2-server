using RO2.Domain.Entities;

namespace RO2.Application.Interfaces;

/// <summary>
/// Persistence of player and system mail (the <c>mails</c> store). One store behind every mail writer: player
/// sends, auction deliveries, the Illusion Pyramid fallback reward and the Dungeon Finder reward all create
/// mail through <see cref="AddAsync"/>, which draws from a single id sequence, so a mail uid is unique across
/// all of them. The claim rules, the mailbox-full rule, packet building and the online notify stay in the Zone host.
/// </summary>
/// <remarks>
/// Phase-1 behaviour is the old store's, unchanged: <see cref="ReplaceAsync"/> is a whole-document write, so a
/// mark-read or claim is a read-modify-write that the caller serialises (the item claim runs under the
/// character's inventory gate; the zeny claim has no guard). The race fixes belong to the next phase.
/// </remarks>
public interface IMailRepository
{
    /// <summary>
    /// Allocates the next mail id from the shared sequence, sets <c>draft.Id</c> and <c>draft.Uid</c> (the
    /// 0x0E4B4B8B high dword over the id's low dword, the handle shape the client expects), stores the mail and returns it.
    /// Callers leave Id and Uid unset. Throws when the sequence or the insert fails; an id consumed by a failed
    /// insert is not reused.
    /// </summary>
    Task<MailMessage> AddAsync(MailMessage draft, CancellationToken ct = default);

    /// <summary>The receiver's newest mails first, at most 200.</summary>
    Task<IReadOnlyList<MailMessage>> GetInboxAsync(uint receiverCharId, CancellationToken ct = default);

    /// <summary>The receiver's mail with that uid, or null.</summary>
    Task<MailMessage?> GetByUidAsync(uint receiverCharId, ulong uid, CancellationToken ct = default);

    /// <summary>How many of the receiver's mails are unread (the 2449 OnMailNew count).</summary>
    Task<long> CountUnreadAsync(uint receiverCharId, CancellationToken ct = default);

    /// <summary>
    /// The mails the receiver holds that were sent at or after <paramref name="notBefore"/>: every kind, read or
    /// not, claimed or not. The mailbox-full test of a player send counts this with notBefore = now minus the
    /// 30-day mail lifetime (mail older than that is never listed, and this server does not delete expired mail).
    /// </summary>
    Task<long> CountLiveInboxAsync(uint receiverCharId, DateTime notBefore, CancellationToken ct = default);

    /// <summary>Overwrites the stored mail whose <c>Id</c> is <c>mail.Id</c> with <paramref name="mail"/> (whole document).</summary>
    Task ReplaceAsync(MailMessage mail, CancellationToken ct = default);

    /// <summary>Deletes the receiver's mail with that uid. False when there was none.</summary>
    Task<bool> DeleteAsync(uint receiverCharId, ulong uid, CancellationToken ct = default);

    /// <summary>
    /// Claims the item attachment with one guarded flip of <c>ItemClaimed</c> (false to true, and an item must be
    /// attached), so of any number of racing claims exactly one gets the mail back and the rest get null. The
    /// returned mail carries the attachment snapshot and <c>ItemClaimed == true</c>. Null when the mail is missing,
    /// not the receiver's, has no item, or was already claimed. The default body is the non-atomic read-modify-write
    /// for test fakes; both providers override it.
    /// </summary>
    async Task<MailMessage?> TryClaimItemAsync(uint receiverCharId, ulong uid, CancellationToken ct = default)
    {
        var mail = await GetByUidAsync(receiverCharId, uid, ct);
        if (mail is null || mail.ItemClaimed || mail.AttachedItemId == 0) return null;
        mail.ItemClaimed = true;
        await ReplaceAsync(mail, ct);
        return mail;
    }

    /// <summary>
    /// Claims the zeny attachment with one guarded flip of <c>GoldClaimed</c> (false to true, and <c>Gold &gt; 0</c>),
    /// with the same exactly-one-winner contract as <see cref="TryClaimItemAsync"/>. The returned mail carries
    /// <c>Gold</c> and <c>GoldClaimed == true</c>; null when there is nothing to claim.
    /// </summary>
    async Task<MailMessage?> TryClaimGoldAsync(uint receiverCharId, ulong uid, CancellationToken ct = default)
    {
        var mail = await GetByUidAsync(receiverCharId, uid, ct);
        if (mail is null || mail.GoldClaimed || mail.Gold <= 0) return null;
        mail.GoldClaimed = true;
        await ReplaceAsync(mail, ct);
        return mail;
    }

    /// <summary>
    /// Deletes the mail only if every attachment on it has been claimed, so a claim that raced another write can
    /// never delete an item or zeny still waiting on it. False when nothing was deleted.
    /// </summary>
    Task<bool> DeleteIfSpentAsync(uint receiverCharId, ulong uid, CancellationToken ct = default);
}
