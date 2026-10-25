using RO2.BuildingBlocks;
using RO2.Economy.Domain;

namespace RO2.Economy.Application;

/// <summary>A buyer's purchase of listings from a stall, already past the host's eligibility gates (shop open, buyer
/// viewing, not the owner) and with the picked listings resolved from the stall's book.</summary>
/// <param name="BuyerCharacterId">The buyer's character id.</param>
/// <param name="SellerCharacterId">The stall owner's character id.</param>
/// <param name="Picked">The picked listings, in pick order, without repeats.</param>
public sealed record StallBuyCommand(uint BuyerCharacterId, uint SellerCharacterId, IReadOnlyList<StallListing> Picked);

/// <summary>What a stall buy did, with what the host needs to build its frames.</summary>
/// <param name="Refusal">0 when the purchase went through, else the 2659 refusal code (see <see cref="StallCodes"/>); nothing moved.</param>
/// <param name="Reason">With a refusal: why, for the host's log.</param>
public sealed record StallBuyResult(ushort Refusal, string Reason)
{
    /// <summary>True when the purchase went through.</summary>
    public bool Succeeded => Refusal == 0;

    /// <summary>With success: the sum of the bought listings' prices, the zeny that moved.</summary>
    public ulong Total { get; init; }

    /// <summary>With success: the buyer's balance right after the payment.</summary>
    public long BuyerGoldAfter { get; init; }

    /// <summary>With success: the seller's balance right after the payment.</summary>
    public long SellerGoldAfter { get; init; }

    /// <summary>With success: the template id of each bought listing, in pick order.</summary>
    public IReadOnlyList<uint> SoldItemIds { get; init; } = [];
}

/// <summary>
/// The buy of a private player shop (stall) as application logic, in the order the original handler ran it. It knows
/// nothing of the wire, the player object or the logger: the host supplies the wallets, the inventory port and the
/// wallets' persistence, and builds and sends every frame from the result. The service itself sends nothing.
/// </summary>
public sealed class StallBuyApplicationService
{
    /// <summary>
    /// Carries out a stall buy: resolves which picked listings are still live, totals them, checks the buyer can pay,
    /// plans where the items land in the buyer's bag, then, under <paramref name="walletGate"/>, checks the seller can
    /// receive the zeny, debits the buyer and credits the seller; then moves the items, removes the listings, and
    /// finally persists the buyer's and then the seller's balance. Every refusal happens before anything is changed.
    /// </summary>
    /// <remarks>
    /// There is NO compensation: once the zeny has changed hands, a failing item write or persist propagates with the
    /// buyer already debited, the seller already credited, and whatever was written left in place (the listings are removed
    /// only after the item writes, so a failed write leaves them listed). The buy takes no per-character inventory gate;
    /// the host's stall purchase gate is the only serialization.
    /// </remarks>
    /// <param name="cmd">The purchase.</param>
    /// <param name="buyerWallet">The buyer's wallet.</param>
    /// <param name="sellerWallet">The stall owner's wallet.</param>
    /// <param name="walletGate">The lock that serializes every wallet change of a stall sale (shared with the other stall flows), held across the seller-limit check, the debit and the credit.</param>
    /// <param name="inventory">The inventory and listing-book port, one instance per buy.</param>
    /// <param name="persistBuyer">Writes the buyer's balance (given the balance right after the payment) through.</param>
    /// <param name="persistSeller">Writes the seller's balance (given the balance right after the payment) through.</param>
    public async Task<StallBuyResult> BuyAsync(StallBuyCommand cmd, IWallet buyerWallet, IWallet sellerWallet, object walletGate,
        IStallBuyInventory inventory, Func<long, Task> persistBuyer, Func<long, Task> persistSeller)
    {
        var sold = await inventory.ResolveLiveAsync(cmd.SellerCharacterId, cmd.Picked);
        if (sold.Count != cmd.Picked.Count)
            return Refused(StallCodes.ResultAlreadySold, "a listed item left the seller's bag");

        if (!StallPricing.TryTotal(sold.Select(s => s.Listing.Price), out var total))
            return Refused(StallCodes.ResultNotEnoughMoney, "the total overflows");
        if (!StallPricing.CanAfford(total, buyerWallet.Gold))
            return Refused(StallCodes.ResultNotEnoughMoney, $"{buyerWallet.Gold} zeny for {total}");

        var bag = await inventory.ReadBuyerBagAsync(cmd.BuyerCharacterId);
        var stackMax = sold.DistinctBy(s => s.ItemId).ToDictionary(s => s.ItemId, s => s.StackMax);
        var plan = TradePlanner.PlanPlacement(
            sold.Select(s => new TradePlanItem(s.Listing.Uid, s.ItemId, s.HeldCount, s.Listing.Count)).ToList(),
            bag.Stacks, bag.FreeCells, [], itemId => stackMax[itemId]);
        if (plan is null)
            return Refused(StallCodes.ResultNoBagRoom, "no free bag cell");

        long price = (long)total;
        long buyerAfter, sellerAfter;
        lock (walletGate)
        {
            if (StallPricing.ExceedsSellerLimit(sellerWallet.Gold, price))
                return Refused(StallCodes.ResultSellerMoneyLimit, $"seller wallet {sellerWallet.Gold} + {total}");
            if (!buyerWallet.TryDebit(price))
                return Refused(StallCodes.ResultNotEnoughMoney, $"{buyerWallet.Gold} zeny for {total}");
            sellerWallet.Credit(price);
            buyerAfter = buyerWallet.Gold;
            sellerAfter = sellerWallet.Gold;
        }

        await inventory.ApplyAsync(cmd.BuyerCharacterId, sold, plan);
        inventory.RemoveListings(sold.Select(s => s.Listing.Index).ToList());

        await persistBuyer(buyerAfter);
        await persistSeller(sellerAfter);

        return new StallBuyResult(0, "")
        {
            Total = total,
            BuyerGoldAfter = buyerAfter,
            SellerGoldAfter = sellerAfter,
            SoldItemIds = sold.Select(s => s.ItemId).ToList(),
        };
    }

    private static StallBuyResult Refused(ushort code, string reason) => new(code, reason);
}
