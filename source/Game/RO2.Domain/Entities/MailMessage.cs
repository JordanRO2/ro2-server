namespace RO2.Domain.Entities;

/// <summary>
/// A stored mail. Persisted to the <c>mails</c> collection in the local dev Mongo (db ro2_server).
/// <see cref="Id"/> maps to Mongo <c>_id</c>; <see cref="Uid"/> is the 8-byte handle echoed to the
/// client in every list row and every C2S mail request (the round-trip key).
/// </summary>
public sealed class MailMessage
{
 /// <summary>Monotonic numeric id (also the low dword of <see cref="Uid"/>). Maps to Mongo _id.</summary>
    public ulong Id { get; set; }

 /// <summary>8-byte mail handle sent on the wire and echoed back by the client.</summary>
    public ulong Uid { get; set; }

    public uint ReceiverCharId { get; set; }
    public string ReceiverName { get; set; } = string.Empty;

    public uint SenderCharId { get; set; }
    public string SenderName { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;

    
    public ulong AttachedItemUid { get; set; }
    public uint AttachedItemId { get; set; }
    public int AttachedItemCount { get; set; }
    public byte AttachedItemRefine { get; set; }
    public short AttachedItemDurability { get; set; } = -1;
    public string? AttachedItemCardsJson { get; set; }

 /// <summary>
 /// The attached item's bestowed element (<see cref="RO2.Inventory.Domain.InventoryItem.Element"/>, record+68).
 /// Kept so an elemented item comes back out of the mail (and out of an auction, whose items leave by
 /// mail) with its element: the claim re-creates the bag row from these snapshot fields only.
 /// Mails stored before this field existed load with 0, the neutral element.
 /// </summary>
    public byte AttachedItemElement { get; set; }

 /// <summary>
 /// The attached item's dye colour and rune-hole punchers (<see cref="RO2.Inventory.Domain.InventoryItem.DyeColor"/>,
 /// record+23, and <see cref="RO2.Inventory.Domain.InventoryItem.SocketPunchersJson"/>, hole+3), kept for the same
 /// reason as <see cref="AttachedItemElement"/>: the claim re-creates the item from these snapshot fields, and
 /// retail repeats both unchanged on every later record of the uid. Mails stored before these fields existed
 /// load with 0 / null, the neutral values.
 /// </summary>
    public uint AttachedItemDyeColor { get; set; }
    public string? AttachedItemSocketPunchersJson { get; set; }
 /// <summary>
 /// The rest of the attached item's per-instance state (InventoryItem.DyeColor2/DyeColor3 at record+101/+105,
 /// FxEnchantIndex at +113, EnchantReinforceId/EnchantSeconds at +17/+19, and the bind history Bound),
 /// carried for the same reason; rows stored before these existed load with 0 / false.
 /// </summary>
    public uint AttachedItemDyeColor2 { get; set; }
    public uint AttachedItemDyeColor3 { get; set; }
    public uint AttachedItemFxEnchantIndex { get; set; }
    public ushort AttachedItemEnchantReinforceId { get; set; }
    public ushort AttachedItemEnchantSeconds { get; set; }
    public bool AttachedItemBound { get; set; }
 /// <summary>
 /// The attached item's own ability value (InventoryItem.AbilityValue, record+69), null = its template's
 /// Abili_val. Kept for the same reason: a stack bought from an NPC carries 0 there (retail 2375 records) and the
 /// claim must re-create it with that. Mails stored before this existed load with null, the template's value.
 /// </summary>
    public uint? AttachedItemAbilityValue { get; set; }

    public bool ItemClaimed { get; set; }

    
    public long Gold { get; set; }
    public bool GoldClaimed { get; set; }

    public bool IsRead { get; set; }
    public DateTime SentAt { get; set; } = DateTime.UtcNow;

 /// <summary>
 /// 2444 list-row kind (rec+8). 0 = player mail (the default, and every mail stored before this field
 /// existed). Auction mails carry their own kind, and the client writes their text from it, so every row of
 /// kinds 2, 7, 10 and 11 has an empty sender and title. See <c>AuctionMailKind</c> in the Zone host.
 /// </summary>
    public byte Kind { get; set; }

 /// <summary>
 /// The item template a system mail is about (2444 rec+58, which the client reads only for kinds
 /// 1..11). Kept apart from <see cref="AttachedItemId"/> because a proceeds mail (kind 10) names the
 /// sold template while carrying no item (rec+58 set, no item uid).
 /// </summary>
    public uint KindItemId { get; set; }

 /// <summary>
 /// The price of the article an auction mail is about: its instant price (AuctionArticle.BuyoutPrice,
 /// entry+142), set by AuctionService when it creates the mail. 2446 OnMailRead shows it at record+173.
 /// It is non-zero on every auction read (kinds 2, 7, 10 and 11) and 0 on every other read. Where the
 /// article is known it is that entry's +142, never its +134 start price. A kind-10
 /// proceeds mail shows the price the article sold at, not its zeny (AuctionService.SellerProceeds of it).
 /// 0 on player mail, which is never set, and on auction mails stored before this field existed.
 /// </summary>
    public long AuctionPrice { get; set; }

 /// <summary>True while any attachment is still waiting to be claimed (drives can-delete / claim UI).</summary>
    public bool HasUnclaimedAttachment =>
        (AttachedItemId != 0 && !ItemClaimed) || (Gold > 0 && !GoldClaimed);
}
