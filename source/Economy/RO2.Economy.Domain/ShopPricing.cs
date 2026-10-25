namespace RO2.Economy.Domain;

/// <summary>
/// Pure NPC-shop price arithmetic: the zeny cost of a buy, the quantity a sell actually moves, and the proceeds of a
/// sell. Kept in the same integer widths the host uses, so the server agrees with the wire to the byte.
/// </summary>
public static class ShopPricing
{
    /// <summary>The zeny a buy costs: the row's buy price times the quantity. (A token buy costs no zeny; the host
    /// decides which applies.)</summary>
    public static long BuyCost(long unitBuyPrice, long quantity) => unitBuyPrice * quantity;

    /// <summary>The quantity a sell actually moves: never more than the stack holds, so selling more than is owned
    /// cannot mint gold.</summary>
    public static int SellQty(int requested, int held) => System.Math.Min(requested, held);

    /// <summary>The proceeds of a sell: the item's own sell price (ItemInfo Price_Sell, not half the buy price)
    /// times the quantity sold, in 64-bit so a large stack cannot overflow.</summary>
    public static long SellProceeds(long unitSellPrice, int quantitySold) => unitSellPrice * quantitySold;
}
