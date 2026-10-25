using RO2.BuildingBlocks;

namespace RO2.Economy.Domain;

/// <summary>Lifecycle of a consignment article. Only <see cref="Active"/> articles are listed or buyable.</summary>
public enum AuctionArticleState : byte
{
    /// <summary>The listing is live.</summary>
    Active = 0,
    /// <summary>The listing sold.</summary>
    Sold = 1,
    /// <summary>The listing was cancelled by the seller.</summary>
    Cancelled = 2,
    /// <summary>The listing expired unsold.</summary>
    Expired = 3,
}

/// <summary>
/// One consignment-sale article, persisted to the <c>auction_articles</c> collection. The listed item is
/// taken OUT of the seller's inventory when the article is created and lives here as a snapshot until it is
/// sold, cancelled or expires, when it leaves again as a mail attachment — so it is never in two places.
/// </summary>
public sealed class AuctionArticle
{
    /// <summary>The additive base of an article's display uid, distinct from an inventory item uid.</summary>
    public const ulong UidBase = 1_040_000_000_000_000_000UL;

    /// <summary>Derives an article's display uid from its article id.</summary>
    public static ulong MakeUid(ulong id) => UidBase + id;

    /// <summary>The listed item's per-instance state as a neutral snapshot, for a mail attachment or a bag restore.</summary>
    public ItemStackSnapshot ToStackSnapshot() => new()
    {
        Id = ItemUid,
        ItemId = ItemId,
        Count = Count,
        Refine = Refine,
        Element = Element,
        Durability = Durability,
        DyeColor = DyeColor,
        DyeColor2 = DyeColor2,
        DyeColor3 = DyeColor3,
        FxEnchantIndex = FxEnchantIndex,
        EnchantReinforceId = EnchantReinforceId,
        EnchantSeconds = EnchantSeconds,
        CardsJson = CardsJson,
        SocketPunchersJson = SocketPunchersJson,
        Bound = Bound,
        AbilityValue = AbilityValue,
    };

    /// <summary>Monotonic numeric id from <c>auction_counters</c>. Maps to Mongo _id.</summary>
    public ulong Id { get; set; }

    /// <summary>The 8-byte article key on the wire (entry+0, echoed at blob+8 of 1405/1406).</summary>
    public ulong Uid { get; set; }

    /// <summary>The seller's character id.</summary>
    public uint SellerCharId { get; set; }
    /// <summary>The seller's character name.</summary>
    public string SellerName { get; set; } = string.Empty;

    /// <summary>The seller's 8-byte character uid (entry+194).</summary>
    public ulong SellerCharUid { get; set; }

    // Item snapshot (the same fields MailMessage keeps for an attachment).
    /// <summary>The listed item's unique instance id.</summary>
    public ulong ItemUid { get; set; }
    /// <summary>The listed item's template id.</summary>
    public uint ItemId { get; set; }
    /// <summary>The stack size listed.</summary>
    public int Count { get; set; }
    /// <summary>The item's refine level.</summary>
    public byte Refine { get; set; }
    /// <summary>The item's durability.</summary>
    public short Durability { get; set; } = -1;
    /// <summary>The socketed cards, as JSON.</summary>
    public string? CardsJson { get; set; }

    /// <summary>
    /// The listed item's bestowed element (<c>InventoryItem.Element</c>, item record+68). The
    /// search entries carry it: a non-zero element sits at entry+8+68 of the 2456 entries. Without it here the item
    /// that comes back by mail (cancel, expiry, purchase) would be re-created neutral.
    /// Articles stored before this field existed load with 0.
    /// </summary>
    public byte Element { get; set; }

    /// <summary>
    /// The listed item's dye colour (<c>InventoryItem.DyeColor</c>, record+0x17) and rune-hole punchers
    /// (<c>InventoryItem.SocketPunchersJson</c>, hole+3). Kept for the same reason as <see cref="Element"/>:
    /// 2456 entries carry filled rune-hole blocks with the puncher template at hole+3,
    /// and the item that comes back by mail is
    /// re-created from this snapshot. Articles stored before these fields existed load with 0 / null.
    /// </summary>
    public uint DyeColor { get; set; }
    /// <summary>The rune-hole punchers, as JSON.</summary>
    public string? SocketPunchersJson { get; set; }

    /// <summary>
    /// The rest of the listed item's per-instance state (InventoryItem.DyeColor2/DyeColor3 at record+0x65/+0x69,
    /// FxEnchantIndex at +113, EnchantReinforceId/EnchantSeconds at +0x11/+0x13, and the bind history Bound),
    /// carried for the same reason; rows stored before these existed load with 0 / false.
    /// </summary>
    public uint DyeColor2 { get; set; }
    /// <summary>The item's third dye colour.</summary>
    public uint DyeColor3 { get; set; }
    /// <summary>The item's FX-enchant index.</summary>
    public uint FxEnchantIndex { get; set; }
    /// <summary>The item's enchant/reinforce id.</summary>
    public ushort EnchantReinforceId { get; set; }
    /// <summary>Remaining timed-enchant seconds.</summary>
    public ushort EnchantSeconds { get; set; }
    /// <summary>Whether the item is account/character bound.</summary>
    public bool Bound { get; set; }

    /// <summary>The listed item's own ability value (InventoryItem.AbilityValue, record+69), null = its template's
    /// Abili_val; carried so a stack bought from an NPC (0 there) comes back by mail with it. Articles stored before
    /// this existed load with null.</summary>
    public uint? AbilityValue { get; set; }

    /// <summary>Auction starting price (entry+134, from 1403 blob+20).</summary>
    public long StartPrice { get; set; }

    /// <summary>Instant ("selling") price (entry+142, from 1403 blob+28). 0 = no buyout.</summary>
    public long BuyoutPrice { get; set; }

    /// <summary>When the listing started (UTC).</summary>
    public DateTime StartedAt { get; set; }
    /// <summary>When the listing expires (UTC).</summary>
    public DateTime ExpiresAt { get; set; }
    /// <summary>The article's current state.</summary>
    public AuctionArticleState State { get; set; }
    /// <summary>The buyer's character id (0 until sold).</summary>
    public uint BuyerCharId { get; set; }
    /// <summary>When the listing closed — sold, cancelled or expired (UTC).</summary>
    public DateTime? ClosedAt { get; set; }

    // The top bid (1407 ReqAuctionTenderJoin), written only by IAuctionRepository's bid operations
    // (TryPlaceBidAsync / TryWinAtInstantPriceAsync / TryAcceptTopBidAsync), each conditional on the bid it read.
    // The bidder's zeny for it is escrowed (taken from the wallet when the bid is placed) and leaves the article
    // either to the seller (the bid wins: 1404, the instant price reached, or expiry) or back to the bidder
    // by mail (outbid, instant-bought by someone else, or cancelled). Articles stored before these fields
    // existed have no bid: they load with 0 / empty, and the repository filters treat the missing field as 0.

    /// <summary>The top bid in zeny; 0 = no bid. entry+134 shows it once set (<c>AuctionPackets.CurrentPrice</c>).</summary>
    public long TopBid { get; set; }

    /// <summary>The top bidder's character id; 0 = no bid.</summary>
    public uint TopBidderCharId { get; set; }

    /// <summary>The top bidder's name: entry+150 of the SELLER's own list (2455), where it is the bidder column.</summary>
    public string TopBidderName { get; set; } = string.Empty;

    /// <summary>The top bidder's 8-byte character uid: entry+202 of every entry.</summary>
    public ulong TopBidderCharUid { get; set; }
}
