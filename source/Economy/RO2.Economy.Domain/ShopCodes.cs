namespace RO2.Economy.Domain;

/// <summary>
/// The NPC-shop protocol result codes (2375 buy, 2376 sell, 2377 buy-back cancel). They are the single source: the
/// host wire layer aliases these, so the shop application flow and the packet builders agree by value. Each keeps the
/// exact width it is encoded at — a widened type would change the bytes on the wire.
/// </summary>
public static class ShopCodes
{
    /// <summary>2375 generic buy failure (Buy_Type 1, Dia, 7 and zeny rows). A system-message id the client resolves.</summary>
    public const ushort BuyFailGeneric = 1;

    /// <summary>2375 failure for a Mastery-point row (Buy_Type 4).</summary>
    public const ushort BuyFailMastery = 20084;

    /// <summary>2375 failure for a buy the bag has no free cell for (the "no free bag cell" code, shared with 2438/2801).</summary>
    public const ushort BuyFailBagFull = 20020;

    /// <summary>2375 failure for a buy past the item's possession limit.</summary>
    public const ushort BuyFailPossession = 20166;

    /// <summary>2376 result: the sale went through.</summary>
    public const ushort SellResultSold = 0;

    /// <summary>2376 result: nothing was sold.</summary>
    public const ushort SellResultNothingSold = 1;

    /// <summary>2377 buy-back refusal (a system-message id the client resolves). Encoded as a signed 16-bit value.</summary>
    public const short CancelRefused = 1;

    /// <summary>2377 buy-back refusal for no free bag cell (20020, the same "no free bag cell" code, as a signed 16-bit value).</summary>
    public const short CancelBagFull = 20020;
}
