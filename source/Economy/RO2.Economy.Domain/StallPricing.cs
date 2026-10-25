namespace RO2.Economy.Domain;

/// <summary>
/// Pure stall price arithmetic. A listing carries the price of the WHOLE listing (not a unit price), so a buy costs the
/// sum of the picked listings' prices, in 64-bit unsigned with an overflow refusal, kept in the same widths the host
/// uses so the server agrees with the wire to the byte.
/// </summary>
public static class StallPricing
{
    /// <summary>
    /// The sum of the picked listings' prices. False when adding a price would pass <see cref="ulong.MaxValue"/>
    /// (the purchase is refused as unaffordable); <paramref name="total"/> is then the sum so far.
    /// </summary>
    /// <param name="prices">The picked listings' prices, in pick order.</param>
    /// <param name="total">The sum.</param>
    public static bool TryTotal(IEnumerable<ulong> prices, out ulong total)
    {
        total = 0;
        foreach (var price in prices)
        {
            if (price > ulong.MaxValue - total) return false;
            total += price;
        }
        return true;
    }

    /// <summary>True when a wallet of <paramref name="buyerGold"/> can pay <paramref name="total"/>: the total fits a
    /// signed 64-bit wallet and does not exceed the balance.</summary>
    /// <param name="total">The sum of the picked prices.</param>
    /// <param name="buyerGold">The buyer's balance.</param>
    public static bool CanAfford(ulong total, long buyerGold) => !(total > long.MaxValue || buyerGold < (long)total);

    /// <summary>True when crediting <paramref name="price"/> to a wallet of <paramref name="sellerGold"/> would pass the
    /// wallet limit (<see cref="long.MaxValue"/>), which refuses the sale.</summary>
    /// <param name="sellerGold">The seller's balance.</param>
    /// <param name="price">The zeny the seller would receive.</param>
    public static bool ExceedsSellerLimit(long sellerGold, long price) => sellerGold > long.MaxValue - price;
}
