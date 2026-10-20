namespace RO2.Domain.Entities;

/// <summary>
/// Character inventory item domain entity
/// </summary>
public class InventoryItem
{
    public ulong Id { get; set; }
    public uint CharacterId { get; set; }
    public uint ItemId { get; set; }
    public int Count { get; set; } = 1;
    public short Slot { get; set; }
    public byte StorageType { get; set; } 

    
    public byte Refine { get; set; }
 /// <summary>
 /// Element bestowed by an attribute stone (RMI 1662, 0 = neutral, 1-5 = elements). Retail keeps it on
 /// the item and writes it back at record+68 of every later item record.
 /// </summary>
    public byte Element { get; set; }
    public short Durability { get; set; } = -1;

 /// <summary>
 /// Dye colour of dye slot 0 (1575 ReqUseDyeItem / 2835 AnsUseDyeItem), ARGB, 0 = undyed. Retail keeps
 /// it on the item instance and writes it as a u32 of every item record, and every later record of that
 /// uid repeats it unchanged. UseDyeItemHandler sets it for a 1575 whose dye slot (request byte 24) is 0.
 /// </summary>
 /// <remarks>
 /// Stored as a BSON Int64, like <see cref="DyeColor2"/> and <see cref="DyeColor3"/>: every colour is at least
 /// 2^31 (alpha 255), which the default uint serializer's checked Int32 cannot hold. The Zone's
 /// DyeColorBson.Register sets that for every uint member named *DyeColor*, as this project has no MongoDB
 /// reference for an attribute.
 /// </remarks>
    public uint DyeColor { get; set; }

 /// <summary>
 /// Dye colour of dye slot 1. An item has three dye slots; the client stores the 2835 colour per slot and
 /// fills the item's three dye colours from the item record. A dyed slot 1 carries its colour in the record.
 /// </summary>
    public uint DyeColor2 { get; set; }

 /// <summary>Dye colour of dye slot 2, written at record+105; see <see cref="DyeColor2"/>.</summary>
    public uint DyeColor3 { get; set; }

 /// <summary>
 /// The FX-enchant effect on this item (1702 ReqGiveEffectToItem): the FXenchant_INFO <c>index</c>, 0 = none.
 /// Retail writes it as the u32 at record+113, and in the 28-byte worn record of 2315/2504/2848 at +23.
 /// It is an FXenchant_INFO index whose Taget_Item_ID is that record's template.
 /// </summary>
    public uint FxEnchantIndex { get; set; }

 /// <summary>
 /// The timed enchant of a Reinforce_Type-2 ItemReinforceInfo row (blessings, whetstones, oils and
 /// embroideries, applied with 1543 ReqAttachSocket): its <c>Reinforce_ID</c>, 0 = never enchanted. Retail
 /// writes it as the u16 at record+17 in every record, and keeps it there when the timer runs out.
 /// </summary>
    public ushort EnchantReinforceId { get; set; }

 /// <summary>
 /// Seconds left on <see cref="EnchantReinforceId"/>, the u16 at record+19: the row's <c>Duration</c> when
 /// applied, then 10 less per 10 s while the item is worn by a player in the world
 /// (TimedEnchantService). The bonus applies while it is above 0.
 /// </summary>
    public ushort EnchantSeconds { get; set; }

    
    public string? CardsJson { get; set; }

 /// <summary>
 /// Template id of the puncher (ItemInfo Item_Type 30) that opened each runehole, in hole order: a JSON
 /// <c>uint[]</c> parallel to <see cref="CardsJson"/>, in the StatService.ParseSockets / SerializeSockets
 /// format. A missing entry (holes opened before this was kept) is 0. Retail writes it as the u32 at hole+3
 /// of the record's runehole block; it is unchanged by an extraction. It is the puncher the 1542 named: the
 /// hole an answered 1542 opens carries the template of the uid at the request's @0.
 /// </summary>
 /// <remarks>
 /// OpenItemSocketHandler (1542) sets the new hole's entry from the consumed puncher's template, and
 /// ItemWorkbench.ApplyRuneholes writes it at hole+3 of every record of the item (the 2785 answer and,
 /// through WorldEntryService.WriteItemInstance, each 2310/2326/2327/2338/2389 record). Holes opened
 /// before this was kept have no entry and still go out as 0. ExtractSocketHandler (1544) leaves it
 /// alone, as retail keeps the puncher through an extraction.
 /// </remarks>
    public string? SocketPunchersJson { get; set; }

 /// <summary>
 /// The item has been worn at least once (ItemSwapHandler sets it when a 1324 puts it in the equipment window or,
 /// for a bag, in the character bag window), so a bind-on-equip template (ItemInfo.Bind_Type 1) stays bound after
 /// it comes off. WorldEntryService
 /// writes it as bit 2 of record+27 (RecordBindFlags).
 /// </summary>
 /// <remarks>
 /// Retail keeps this per item instance: a worn record (bag byte 0) carries the bit, and a bag record of a uid
 /// that was seen worn earlier has it too. The bit goes 0 to 2 on the 2327 that moves the card into bag 0
 /// slots 20-24 after a 1324, and goes back only on count-0 2379 destroy records. A stack a
 /// worn item joins keeps its own flag (same uid), so this is a per-row flag. Rows stored before it existed
 /// read as false.
 /// </remarks>
    public bool Bound { get; set; }

 /// <summary>
 /// This instance's ability value, the u32 at record+69 (WorldEntryService.RecordAbilityValue), when it is not
 /// its template's ItemInfo.Abili_val; null = the template's value, which is what every row stored before this
 /// existed, and every row no path sets it on, keeps writing.
 /// </summary>
 /// <remarks>
 /// Retail fixes the value per uid (see WorldEntryService.RecordAbilityValue). A stack bought from
 /// an NPC carries 0 there whatever its template's Abili_val: every 2375 OnBuyItem record of a template with
 /// Abili_val != 0 has 0 at record+69. BuyItemHandler sets 0 on the stacks it creates.
 /// </remarks>
    public uint? AbilityValue { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

 /// <summary>
 /// Copies this instance's own state from <paramref name="source"/> onto this row: refine, element,
 /// durability, the three dye colours, the FX-enchant effect, the timed enchant, rune holes
 /// (<see cref="CardsJson"/>), their punchers, the bind history (<see cref="Bound"/>) and the ability value
 /// (<see cref="AbilityValue"/>). Returns this row, so
 /// it chains after an object initializer that sets the placement (owner, count, slot, container).
 /// </summary>
 /// <remarks>
 /// For every path that re-creates an item from another row instead of moving the row itself: the
 /// stack splits (ItemSwapHandler, TradeConfirmHandler), the buy-back (BuyBackLedger.RecordSale,
 /// CancelSellItemHandler) and the trade-window copy (TradeOfferQuantities.WithCount). Retail gives such
 /// an item back with the same state: the bought-back record equals the sold one in every byte except
 /// the count and bag/slot, and a bestowed element is repeated in every later record of its uid.
 /// Listing the fields at each such site is how the element
 /// The stores that keep an item as their own snapshot type rather than an InventoryItem (the guild
 /// vault's GuildStorageItem, the mail's AttachedItem* fields, the AuctionArticle) cannot call this and
 /// carry the same fields by name, so a property added here must be added to those three as well (today all
 /// three carry every field above).
 /// </remarks>
    public InventoryItem CopyInstanceStateFrom(InventoryItem source)
    {
        Refine = source.Refine;
        Element = source.Element;
        Durability = source.Durability;
        DyeColor = source.DyeColor;
        DyeColor2 = source.DyeColor2;
        DyeColor3 = source.DyeColor3;
        FxEnchantIndex = source.FxEnchantIndex;
        EnchantReinforceId = source.EnchantReinforceId;
        EnchantSeconds = source.EnchantSeconds;
        CardsJson = source.CardsJson;
        SocketPunchersJson = source.SocketPunchersJson;
        Bound = source.Bound;
        AbilityValue = source.AbilityValue;
        return this;
    }

    
    public virtual Character Character { get; set; } = null!;
}
