namespace RO2.Economy.Domain;

/// <summary>
/// Pure auction price arithmetic: the current price of an article, the minimum next bid, and the seller's
/// proceeds. Kept in single precision where the client's own arithmetic is, so the server agrees to the byte.
/// </summary>
public static class AuctionPricing
{
    /// <summary>The current price of an article: the top bid once there is one, otherwise the start price.</summary>
    public static long CurrentPrice(AuctionArticle a) => a.TopBidderCharId != 0 ? a.TopBid : a.StartPrice;

    /// <summary>The value 0.1 as a single-precision float (0x3DCCCCCD): exactly 13,421,773 times 2^-27.</summary>
    private const ulong TenthFloatNumerator = 13_421_773;

    /// <summary>Binary exponent of <see cref="TenthFloatNumerator"/>: the value is the numerator divided by 2^27.</summary>
    private const int TenthFloatShift = 27;

    /// <summary>Significand width in bits of a single-precision float, which the client's price arithmetic rounds to.</summary>
    private const int SinglePrecisionBits = 24;

    /// <summary>
    /// The lowest bid accepted on an article with the given current price: the current price plus 10 percent, at
    /// least 1. The product is rounded to single precision, as the client's arithmetic does; bids below are refused.
    /// </summary>
    public static long MinimumBid(long current)
    {
        if (current <= 0) return 1;
        UInt128 n = (UInt128)(ulong)current * TenthFloatNumerator;
        int bits = 128 - (int)UInt128.LeadingZeroCount(n);
        if (bits > SinglePrecisionBits)
        {
            int shift = bits - SinglePrecisionBits;
            UInt128 q = n >> shift;
            UInt128 rem = n & ((UInt128.One << shift) - 1);
            UInt128 half = UInt128.One << (shift - 1);
            if (rem > half || (rem == half && (q & UInt128.One) == UInt128.One)) q++;
            n = q << shift;
        }
        UInt128 truncated = n >> TenthFloatShift; // truncate toward zero
        long step = truncated > (UInt128)long.MaxValue ? long.MaxValue : (long)truncated;
        if (step < 1) step = 1;
        return step > long.MaxValue - current ? long.MaxValue : current + step;
    }

    /// <summary>
    /// What the seller receives for a sale: <paramref name="sellerProceedsRate"/> of the price (the house keeps
    /// the rest), in single precision. The price is rounded to float32 before the multiply too, so the proceeds
    /// are float32(float32(price) × rate). With the retail default rate of 0.95 the house cut is 5%.
    /// </summary>
    public static long SellerProceeds(long price, double sellerProceedsRate)
        => price <= 0 ? 0 : (long)(float)((float)price * sellerProceedsRate);
}
