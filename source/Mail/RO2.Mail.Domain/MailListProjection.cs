namespace RO2.Mails.Domain;

/// <summary>The fields of one 2444 list row, as the wire writes them.</summary>
public readonly record struct MailListRow(
    ulong Uid, byte Kind, bool IsRead, DateTime SentAt, long Gold, ulong ItemUid, uint ItemTemplateId, string SenderName, string Title);

/// <summary>
/// The pure per-row projection of a stored mail onto the 2444 list row. Claimed zeny and a claimed item uid are
/// hidden (0), the template id shows only for kinds 1..11, and sender/title pass through as stored (an auction mail
/// is stored with both empty, a player mail with the real ones). The NPC gate, the inbox fetch and the row bytes stay host.
/// </summary>
public static class MailListProjection
{
    /// <summary>First mail kind whose item template id the client reads from the list row (kinds 1..11).</summary>
    public const byte FirstTemplateMailKind = 1;

    /// <summary>Last mail kind whose item template id the client reads from the list row.</summary>
    public const byte LastTemplateMailKind = 11;

    public static MailListRow Project(MailMessage m) => new(
        m.Uid,
        m.Kind,
        m.IsRead,
        m.SentAt,
        MailRules.HasClaimableGold(m) ? m.Gold : 0,
        MailRules.HasClaimableItem(m) ? m.AttachedItemUid : 0,
        m.Kind is >= FirstTemplateMailKind and <= LastTemplateMailKind ? m.KindItemId : 0,
        m.SenderName,
        m.Title);
}
