namespace RO2.Economy.Domain;

/// <summary>
/// The private player shop (stall) buy result codes carried by 2659 AnsBuyPrivateShopItem. They are the single source:
/// the host wire layer aliases these, so the stall buy flow and the packet builders agree by value. Each keeps the exact
/// width it is encoded at (a 16-bit unsigned result) — a widened type would change the bytes on the wire.
/// </summary>
public static class StallCodes
{
    /// <summary>Refusal: an item on the purchased list was already sold to another buyer.</summary>
    public const ushort ResultAlreadySold = 23006;

    /// <summary>Refusal: the buyer is not the buyer this private shop is open to.</summary>
    public const ushort ResultWrongBuyer = 23019;

    /// <summary>Refusal: the buyer does not have enough room in the bags.</summary>
    public const ushort ResultNoBagRoom = 23020;

    /// <summary>Refusal: the seller's total zeny would exceed the limit.</summary>
    public const ushort ResultSellerMoneyLimit = 23021;

    /// <summary>Refusal: the private shop has closed.</summary>
    public const ushort ResultShopClosed = 23023;

    /// <summary>Refusal: the buyer does not have enough zeny.</summary>
    public const ushort ResultNotEnoughMoney = 23026;
}
