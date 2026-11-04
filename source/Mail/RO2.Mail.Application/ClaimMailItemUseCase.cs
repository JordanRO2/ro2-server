using RO2.Inventory.Domain;
using RO2.Mails.Domain;

namespace RO2.Mails.Application;

/// <summary>
/// What a step of the 1396 item claim decided. The host maps each refusal to its 2447 result code
/// (<c>InvalidMail</c> 16203, <c>SystemError</c> 16201, <c>NoSpace</c> 16207).
/// </summary>
public enum MailClaimVerdict
{
    /// <summary>The step passes; the host goes on to the next one.</summary>
    Accepted,

    /// <summary>Refused: no such mail for the reader, or it has no unclaimed item left (every re-claim, a double click's second 1396).</summary>
    InvalidMail,

    /// <summary>Refused: there is no inventory repository to place the item with.</summary>
    SystemError,

    /// <summary>Refused: the item joins no stack and the bag has no free cell.</summary>
    NoSpace,
}

/// <summary>
/// The decisions of a 1396 mail item claim, in the order the host takes them: whether the mail has an item to claim, then
/// whether the bag can take it. Pure over a mail snapshot and two facts the host resolved: no Mongo, no game-data store
/// and no wire. The host keeps everything that needs either: the joinable-stack search (it resolves the item template's
/// ability value and Stack_Max), the free-cell scan, the claim gate, the atomic <c>ItemClaimed</c> flip FIRST, the
/// provider-aware placement and its uid-conflict retry, the undo of both on an exception, the Khara held-item report
/// (2577, BEFORE the 2447), the 2447 and the delete of a spent auction mail LAST.
/// </summary>
public static class ClaimMailItemUseCase
{
    /// <summary>InventoryItem.StorageType 0: the item sits in a bag.</summary>
    private const byte StorageBag = 0;

    /// <summary>
    /// Whether <paramref name="mail"/> can be claimed at all: it exists, still carries an unclaimed item, and the host has
    /// an inventory repository (<paramref name="inventoryAvailable"/>). A missing mail and a spent one are both 16203.
    /// </summary>
    public static MailClaimVerdict DecideClaimable(MailMessage? mail, bool inventoryAvailable)
    {
        if (mail == null || !MailRules.HasClaimableItem(mail)) return MailClaimVerdict.InvalidMail;
        return inventoryAvailable ? MailClaimVerdict.Accepted : MailClaimVerdict.SystemError;
    }

    /// <summary>
    /// Whether the bag can take the item: it joins a stack the host found (<paramref name="hasJoinStack"/>, which works
    /// in a full bag) or has a free cell (<paramref name="hasFreeCell"/>). Otherwise there is no space.
    /// </summary>
    public static MailClaimVerdict DecidePlacement(bool hasJoinStack, bool hasFreeCell)
        => hasJoinStack || hasFreeCell ? MailClaimVerdict.Accepted : MailClaimVerdict.NoSpace;

    /// <summary>The stack count a mail's item carries (a mail stored with 0 means one).</summary>
    public static int AttachedCount(MailMessage mail) => mail.AttachedItemCount > 0 ? mail.AttachedItemCount : 1;

    /// <summary>
    /// The mailed item as a bag row (StorageType 0) with uid <paramref name="id"/> in flat cell
    /// <paramref name="slot"/>, carrying the snapshot the mail took of it. The single mapping from the
    /// mail's AttachedItem* fields to an item, used by the claim and by the 2446 attachment record,
    /// so the item a mail shows is the item it gives.
    /// </summary>
    public static InventoryItem AttachmentAsBagRow(MailMessage mail, uint charId, ulong id, short slot) => new()
    {
        Id = id,
        CharacterId = charId,
        ItemId = mail.AttachedItemId,
        Count = AttachedCount(mail),
        Slot = slot,
        StorageType = StorageBag,
        Refine = mail.AttachedItemRefine,
        Durability = mail.AttachedItemDurability,
        CardsJson = mail.AttachedItemCardsJson,
        GrindingJson = mail.AttachedItemGrindingJson,
        Element = mail.AttachedItemElement,
        DyeColor = mail.AttachedItemDyeColor,
        SocketPunchersJson = mail.AttachedItemSocketPunchersJson,
        DyeColor2 = mail.AttachedItemDyeColor2,
        DyeColor3 = mail.AttachedItemDyeColor3,
        FxEnchantIndex = mail.AttachedItemFxEnchantIndex,
        EnchantReinforceId = mail.AttachedItemEnchantReinforceId,
        EnchantSeconds = mail.AttachedItemEnchantSeconds,
        Bound = mail.AttachedItemBound,
        AbilityValue = mail.AttachedItemAbilityValue,
    };
}
