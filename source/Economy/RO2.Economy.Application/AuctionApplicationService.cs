using RO2.BuildingBlocks;
using RO2.Economy.Domain;

namespace RO2.Economy.Application;

/// <summary>How an auction listing attempt ended.</summary>
public enum AuctionListOutcome
{
    /// <summary>The item was listed.</summary>
    Listed,
    /// <summary>The seller could not pay the registration fee.</summary>
    NotEnoughMoney,
    /// <summary>The stack was gone from the bag by the time it was taken (a concurrent trade/sale/mail attach).</summary>
    ItemGone,
    /// <summary>Storing the article failed; the item was restored and the fee refunded.</summary>
    InternalError,
}

/// <summary>Pre-validated inputs for listing an item (the host computes the fee and validates against game data).</summary>
public sealed record AuctionListCommand(
    uint SellerCharId, string SellerName, ulong SellerCharUid, ulong ItemUid, int Count,
    long StartPrice, long BuyoutPrice, int Hours, long Fee);

/// <summary>The result of a listing attempt. <see cref="Taken"/> is the escrowed snapshot (so the host can
/// build the client's slot record), and <see cref="CountLeft"/> is what stayed in the bag.</summary>
public sealed record AuctionListResult(AuctionListOutcome Outcome, AuctionArticle? Article, ItemStackSnapshot? Taken, int CountLeft);

/// <summary>
/// The auction flow as pure application logic: it charges the fee through the wallet port, moves the item through
/// the inventory-escrow port, stores the article through the repository, and compensates (restore + refund) if the
/// store fails. It knows nothing of the wire, the player object or the game data; the host validates and builds packets.
/// </summary>
public sealed class AuctionApplicationService(IAuctionRepository repo, IInventoryEscrow escrow)
{
    private readonly IAuctionRepository _repo = repo;
    private readonly IInventoryEscrow _escrow = escrow;

    /// <summary>
    /// Charges <paramref name="cmd"/>'s fee, takes the stack into escrow, stores the article, and returns the
    /// outcome. The fee is taken before anything moves and handed back when nothing is listed. On a store failure
    /// the item is restored and the fee refunded; <paramref name="onStoreError"/> is invoked with the exception so
    /// the host can log it.
    /// </summary>
    public async Task<AuctionListResult> ListAsync(AuctionListCommand cmd, IWallet wallet, Action<Exception>? onStoreError = null)
    {
        // The registration fee is taken atomically before anything moves; the paths below hand it back when
        // nothing was listed.
        if (!wallet.TryDebit(cmd.Fee))
            return new AuctionListResult(AuctionListOutcome.NotEnoughMoney, null, null, 0);

        // Take the item first, so it can never be both in the bag and on sale (see IInventoryEscrow).
        var (taken, countLeft) = await _escrow.TakeAsync(cmd.SellerCharId, cmd.ItemUid, cmd.Count);
        if (taken is null)
        {
            wallet.Credit(cmd.Fee);
            return new AuctionListResult(AuctionListOutcome.ItemGone, null, null, 0);
        }

        try
        {
            // A whole stack keeps its uid; a split-off part needs a uid of its own so the article does not alias
            // the stack still in the bag.
            ulong listedUid = countLeft > 0 ? await _escrow.NextItemUidAsync() : taken.Id;
            var id = await _repo.GetNextIdAsync();
            var now = DateTime.UtcNow;
            var article = new AuctionArticle
            {
                Id = id,
                Uid = AuctionArticle.MakeUid(id),
                SellerCharId = cmd.SellerCharId,
                SellerName = cmd.SellerName,
                SellerCharUid = cmd.SellerCharUid,
                ItemUid = listedUid,
                ItemId = taken.ItemId,
                Count = cmd.Count,
                Refine = taken.Refine,
                Durability = taken.Durability,
                CardsJson = taken.CardsJson,
                Element = taken.Element,
                DyeColor = taken.DyeColor,
                SocketPunchersJson = taken.SocketPunchersJson,
                DyeColor2 = taken.DyeColor2,
                DyeColor3 = taken.DyeColor3,
                FxEnchantIndex = taken.FxEnchantIndex,
                EnchantReinforceId = taken.EnchantReinforceId,
                EnchantSeconds = taken.EnchantSeconds,
                Bound = taken.Bound,
                AbilityValue = taken.AbilityValue,
                StartPrice = cmd.StartPrice,
                BuyoutPrice = cmd.BuyoutPrice,
                StartedAt = now,
                ExpiresAt = now.AddHours(cmd.Hours),
                State = AuctionArticleState.Active,
            };
            await _repo.InsertAsync(article);
            return new AuctionListResult(AuctionListOutcome.Listed, article, taken, countLeft);
        }
        catch (Exception ex)
        {
            onStoreError?.Invoke(ex);
            await _escrow.RestoreAsync(cmd.SellerCharId, taken, countLeft, cmd.Count);
            wallet.Credit(cmd.Fee);   // the fee was taken above and nothing was listed
            return new AuctionListResult(AuctionListOutcome.InternalError, null, taken, countLeft);
        }
    }
}
