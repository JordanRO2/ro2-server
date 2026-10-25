namespace RO2.Economy.Domain;

/// <summary>Pure rules of who may buy from a stall and which listings are still on sale.</summary>
public static class StallBuyRules
{
    /// <summary>
    /// The refusal code for a buyer that may not buy from a stall right now, or 0 when it may: the owner buying from
    /// its own shop or a buyer that never opened it is the wrong buyer; a shop that is not open (or has no record) has closed.
    /// The checks run in that order.
    /// </summary>
    /// <param name="buyerIsOwner">The buyer is the shop's owner.</param>
    /// <param name="shopOpenAndListed">The shop is open and the host holds its listings.</param>
    /// <param name="buyerIsViewer">The buyer opened this shop since it began.</param>
    public static ushort EligibilityRefusal(bool buyerIsOwner, bool shopOpenAndListed, bool buyerIsViewer)
    {
        if (buyerIsOwner) return StallCodes.ResultWrongBuyer;
        if (!shopOpenAndListed) return StallCodes.ResultShopClosed;
        if (!buyerIsViewer) return StallCodes.ResultWrongBuyer;
        return 0;
    }

    /// <summary>
    /// True when a listing can still be sold: the listed instance is still in the seller's bag and still holds at
    /// least the listed count.
    /// </summary>
    /// <param name="inSellerBag">The listed instance exists in the seller's bag.</param>
    /// <param name="heldCount">The count the instance holds now.</param>
    /// <param name="listedCount">The listed count.</param>
    public static bool IsListingLive(bool inSellerBag, int heldCount, int listedCount) => inSellerBag && heldCount >= listedCount;
}
