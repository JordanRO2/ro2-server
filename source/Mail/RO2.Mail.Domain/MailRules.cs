using System.Globalization;

namespace RO2.Mails.Domain;

/// <summary>
/// The pure mail decisions: the send fee and zeny cap, the mailbox limit and lifetime, what a claim can take
/// and when it deletes the mail. Time and config come in as arguments; nothing here reads a store or the wire.
/// </summary>
public static class MailRules
{
    /// <summary>Zeny charged on top of the attached zeny for each successful send. Flat, not a table value.</summary>
    public const long PostageZeny = 10;

    /// <summary>Live mails a recipient may hold before a player-to-player send is refused. System and auction mail are exempt.</summary>
    public const int MailboxPlayerSendLimit = 31;

    /// <summary>Days a mail lives: its expiry is its sent date plus this.</summary>
    public const int MailLifetimeDays = 30;

    /// <summary>Disposition of a successful claim where the mail stays in the mailbox.</summary>
    public const byte KeepMailDisposition = 1;

    /// <summary>Disposition of a claim after which the mail is removed and the client deletes it.</summary>
    public const byte DeleteMailDisposition = 2;

    /// <summary>The attached-zeny cap from the Config_Def 92 value, or no cap when it is absent or unparseable.</summary>
    public static long ZenyCap(string? configValue) =>
        long.TryParse(configValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cap) ? cap : long.MaxValue;

    /// <summary>Whether <paramref name="gold"/> may be attached: not negative and not over <paramref name="cap"/>.</summary>
    public static bool IsAttachableZeny(long gold, long cap) => gold >= 0 && gold <= cap;

    /// <summary>The whole send charge (attached zeny plus postage); false when it would overflow a long.</summary>
    public static bool TryTotalCharge(long gold, out long total)
    {
        total = 0;
        if (gold > long.MaxValue - PostageZeny) return false;
        total = gold + PostageZeny;
        return true;
    }

    /// <summary>The oldest sent date that still counts as live at <paramref name="nowUtc"/>.</summary>
    public static DateTime LiveCutoff(DateTime nowUtc) => nowUtc.AddDays(-MailLifetimeDays);

    /// <summary>A mail is live while it is no older than the lifetime (the repositories count the same <c>SentAt &gt;= cutoff</c>).</summary>
    public static bool IsLive(DateTime sentAtUtc, DateTime nowUtc) => sentAtUtc >= LiveCutoff(nowUtc);

    /// <summary>The expiry date of a mail sent at <paramref name="sentAtUtc"/>.</summary>
    public static DateTime ExpiresAt(DateTime sentAtUtc) => sentAtUtc.AddDays(MailLifetimeDays);

    /// <summary>Whether a recipient holding <paramref name="liveHeld"/> live mails refuses another player send.</summary>
    public static bool IsMailboxFull(long liveHeld) => liveHeld >= MailboxPlayerSendLimit;

    /// <summary>Whether the item on <paramref name="mail"/> can still be claimed.</summary>
    public static bool HasClaimableItem(MailMessage mail) => mail.AttachedItemId != 0 && !mail.ItemClaimed;

    /// <summary>Whether the zeny on <paramref name="mail"/> can still be claimed.</summary>
    public static bool HasClaimableGold(MailMessage mail) => mail.Gold > 0 && !mail.GoldClaimed;

    /// <summary>
    /// Whether a claim that just took an attachment leaves the mail removed: an auction mail (<paramref name="isAuction"/>,
    /// decided by the caller from its kind) with nothing left to claim.
    /// </summary>
    public static bool ClaimRemovesMail(bool isAuction, MailMessage mail) => isAuction && !mail.HasUnclaimedAttachment;

    /// <summary>The disposition a successful claim answers with: delete the mail or keep it.</summary>
    public static byte ClaimDisposition(bool removesMail) => removesMail ? DeleteMailDisposition : KeepMailDisposition;
}
