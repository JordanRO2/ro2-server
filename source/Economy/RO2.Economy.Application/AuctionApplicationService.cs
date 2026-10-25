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
/// The auction economy flows as application logic: listing an item (fee → escrow → store → compensate) and the
/// settlement policy that pays out a sale, cancellation or expiry (who is mailed the item, the proceeds or a
/// refunded bid, and who is notified in-world). It knows nothing of the wire, the player object or the game data;
/// the host validates, orchestrates the wallet and the repository transaction, logs, and builds packets, calling
/// this service for the fee/escrow transaction and the mail/notice settlement.
/// </summary>
/// <param name="repo">Auction article store.</param>
/// <param name="mail">Payout mail port.</param>
/// <param name="notifier">In-world 2693 notice port.</param>
/// <param name="sellerProceedsRate">Fraction of a sale the seller keeps (the house cut is 1 − this).</param>
/// <param name="onAwarded">Invoked after an article is awarded to its top bidder, with the article and the seller's proceeds, so the host can log it.</param>
/// <param name="onRefundMailError">Invoked when a displaced bid's refund mail could not be stored (the failure is swallowed, as it was before), so the host can log it.</param>
/// <param name="onExpirySettleError">Invoked when an expired article could not be mailed during the sweep, so the host can log it.</param>
public sealed class AuctionApplicationService(
    IAuctionRepository repo,
    IEconomyMailPort mail,
    IAuctionNotifier notifier,
    double sellerProceedsRate,
    Action<AuctionArticle, long>? onAwarded = null,
    Action<AuctionArticle, Exception>? onRefundMailError = null,
    Action<AuctionArticle, Exception>? onExpirySettleError = null)
{
    private readonly IAuctionRepository _repo = repo;
    private readonly IEconomyMailPort _mail = mail;
    private readonly IAuctionNotifier _notifier = notifier;
    private readonly double _sellerProceedsRate = sellerProceedsRate;
    private readonly Action<AuctionArticle, long>? _onAwarded = onAwarded;
    private readonly Action<AuctionArticle, Exception>? _onRefundMailError = onRefundMailError;
    private readonly Action<AuctionArticle, Exception>? _onExpirySettleError = onExpirySettleError;

    /// <summary>Articles closed per expiry query; the sweep keeps querying until a batch comes back short.</summary>
    private const int ExpirySweepBatch = 100;

    /// <summary>What the seller receives for a sale (<see cref="AuctionPricing.SellerProceeds"/>), at the configured rate.</summary>
    public long SellerProceeds(long price) => AuctionPricing.SellerProceeds(price, _sellerProceedsRate);

    /// <summary>Notifies a character of new mail (2449) if online.</summary>
    public Task NotifyNewMailAsync(uint charId) => _mail.NotifyNewMailAsync(charId);

    /// <summary>
    /// Charges <paramref name="cmd"/>'s fee, takes the stack into escrow through <paramref name="escrow"/>, stores
    /// the article, and returns the outcome. The fee is taken before anything moves and handed back when nothing is
    /// listed. On a store failure the item is restored and the fee refunded; <paramref name="onStoreError"/> is
    /// invoked with the exception so the host can log it.
    /// </summary>
    public async Task<AuctionListResult> ListAsync(AuctionListCommand cmd, IWallet wallet, IInventoryEscrow escrow, Action<Exception>? onStoreError = null)
    {
        // The registration fee is taken atomically before anything moves; the paths below hand it back when
        // nothing was listed.
        if (!wallet.TryDebit(cmd.Fee))
            return new AuctionListResult(AuctionListOutcome.NotEnoughMoney, null, null, 0);

        // Take the item first, so it can never be both in the bag and on sale (see IInventoryEscrow).
        var (taken, countLeft) = await escrow.TakeAsync(cmd.SellerCharId, cmd.ItemUid, cmd.Count);
        if (taken is null)
        {
            wallet.Credit(cmd.Fee);
            return new AuctionListResult(AuctionListOutcome.ItemGone, null, null, 0);
        }

        try
        {
            // A whole stack keeps its uid; a split-off part needs a uid of its own so the article does not alias
            // the stack still in the bag.
            ulong listedUid = countLeft > 0 ? await escrow.NextItemUidAsync() : taken.Id;
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
            await escrow.RestoreAsync(cmd.SellerCharId, taken, countLeft, cmd.Count);
            wallet.Credit(cmd.Fee);   // the fee was taken above and nothing was listed
            return new AuctionListResult(AuctionListOutcome.InternalError, null, taken, countLeft);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Settlement: who is mailed the item, the proceeds or a refunded bid, and who is notified in-world,
    // when an article sells, is cancelled or expires. The host flows orchestrate the wallet and the
    // repository transaction and call these to pay out.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Stores a mail carrying the article's item and/or zeny for <paramref name="receiverCharId"/>. Sender and
    /// title stay empty: auction mails carry empty sender and title strings and let the client write the text from
    /// the list-row kind and the item template, which is the article's template on every such row, the item-less
    /// proceeds mail included. Every kind also keeps the article's instant price, the value the client shows on
    /// each of them — on the proceeds mail too, where it is the price the article sold at, not the zeny it carries.
    /// </summary>
    public Task DeliverAsync(uint receiverCharId, string receiverName, AuctionArticle article, byte kind, bool withItem, long gold, DateTime sentAtUtc)
        => _mail.SendAuctionMailAsync(receiverCharId, receiverName, kind, article.BuyoutPrice, gold,
            withItem ? article.ToStackSnapshot() : null, article.ItemId, sentAtUtc);

    /// <summary>The article's item back to its seller, with the given kind, stamped <paramref name="sentAtUtc"/>.</summary>
    public Task ReturnItemToSellerAsync(AuctionArticle article, byte kind, DateTime sentAtUtc)
        => DeliverAsync(article.SellerCharId, article.SellerName, article, kind, withItem: true, gold: 0, sentAtUtc);

    /// <summary>2693 type 1 to the seller, if online: the article, the item template and the proceeds.</summary>
    public Task NotifySellerSoldAsync(AuctionArticle sold, long proceeds)
        => _notifier.SendSystemMessageAsync(sold.SellerCharId, AuctionCodes.NotifySold, sold.Uid, sold.ItemId, proceeds);

    /// <summary>Sends a 2693 system notice about an article to a character, if online.</summary>
    public Task SendSystemMessageAsync(uint charId, uint type, AuctionArticle article, long amount)
        => _notifier.SendSystemMessageAsync(charId, type, article.Uid, article.ItemId, amount);

    /// <summary>
    /// Mails a displaced top bid back to its bidder, optionally sends that bidder a notice, and notifies new mail.
    /// The notice amount is 0; the refund itself arrives by mail. A mail failure is swallowed (reported through the
    /// service's refund-mail-error callback), as it was before: a refund that cannot be mailed must not throw.
    /// </summary>
    public async Task RefundTopBidAsync(AuctionArticle displaced, byte mailKind, uint notifyType, bool notifyBidder, DateTime nowUtc)
    {
        if (displaced.TopBidderCharId == 0 || displaced.TopBid <= 0) return;
        try
        {
            await DeliverAsync(displaced.TopBidderCharId, displaced.TopBidderName, displaced, mailKind, withItem: false,
                gold: displaced.TopBid, nowUtc);
        }
        catch (Exception ex)
        {
            _onRefundMailError?.Invoke(displaced, ex);
            return;
        }
        if (notifyBidder)
            await SendSystemMessageAsync(displaced.TopBidderCharId, notifyType, displaced, 0);
        await _mail.NotifyNewMailAsync(displaced.TopBidderCharId);
    }

    /// <summary>
    /// Delivers an article sold to its top bidder (by a bid at the instant price or by expiry with a bid): the item
    /// to the bidder, the proceeds (less the seller share) to the seller, and a notice to each. The bid's zeny was
    /// taken when the bid was placed. Both receivers are added to <paramref name="mailReceivers"/>.
    /// </summary>
    public async Task AwardToTopBidderAsync(AuctionArticle sold, DateTime nowUtc, bool notifyBidder, HashSet<uint> mailReceivers)
    {
        await DeliverAsync(sold.TopBidderCharId, sold.TopBidderName, sold, AuctionMailKinds.BidWon, withItem: true, gold: 0, nowUtc);
        mailReceivers.Add(sold.TopBidderCharId);

        long proceeds = SellerProceeds(sold.TopBid);
        if (proceeds > 0)
        {
            await DeliverAsync(sold.SellerCharId, sold.SellerName, sold, AuctionMailKinds.BidProceeds, withItem: false, gold: proceeds, nowUtc);
            mailReceivers.Add(sold.SellerCharId);
        }
        await SendSystemMessageAsync(sold.SellerCharId, AuctionCodes.NotifyBidSold, sold, proceeds);
        if (notifyBidder)
            await SendSystemMessageAsync(sold.TopBidderCharId, AuctionCodes.NotifyBidWon, sold, 0);

        _onAwarded?.Invoke(sold, proceeds);
    }

    /// <summary>
    /// Settles an article that the repository just closed at expiry. Without a bid its item goes back to the seller
    /// (<see cref="AuctionMailKinds.Expired"/>); with one, the top bidder wins it at the top bid, since the bid's
    /// zeny was taken when it was placed. Every receiver's id goes into <paramref name="mailReceivers"/>.
    /// </summary>
    public async Task SettleExpiredAsync(AuctionArticle closed, DateTime nowUtc, HashSet<uint> mailReceivers)
    {
        if (closed.TopBidderCharId != 0)
        {
            await AwardToTopBidderAsync(closed, nowUtc, notifyBidder: true, mailReceivers);
            return;
        }
        await ReturnItemToSellerAsync(closed, AuctionMailKinds.Expired, nowUtc);
        mailReceivers.Add(closed.SellerCharId);
    }

    /// <summary><see cref="SettleExpiredAsync"/> for one article, with its 2449s sent at once.</summary>
    public async Task SettleExpiredNowAsync(AuctionArticle closed, DateTime nowUtc)
    {
        var receivers = new HashSet<uint>();
        await SettleExpiredAsync(closed, nowUtc, receivers);
        foreach (var receiver in receivers)
            await _mail.NotifyNewMailAsync(receiver);
    }

    /// <summary>
    /// Returns every article due by <paramref name="nowUtc"/>, in batches: each is closed atomically and settled
    /// (its item mailed back, or awarded to its top bidder). A settle failure is reported through the service's
    /// expiry-settle-error callback and the article is not re-opened. Returns how many were returned.
    /// </summary>
    public async Task<int> ReturnExpiredAsync(DateTime nowUtc)
    {
        int returned = 0;
        var receivers = new HashSet<uint>();
        while (true)
        {
            var due = await _repo.GetDueAsync(nowUtc, ExpirySweepBatch);
            int closedThisBatch = 0;
            foreach (var article in due)
            {
                var closed = await _repo.TryExpireAsync(article.Uid, nowUtc);
                if (closed == null) continue; // closed another way in between
                closedThisBatch++;
                try
                {
                    await SettleExpiredAsync(closed, nowUtc, receivers);
                    returned++;
                }
                catch (Exception ex)
                {
                    // Not re-opened: if the insert did land, a retry would mail the item twice. The article
                    // document keeps the whole item snapshot and the top bid (state Expired), so it can be
                    // recovered by hand.
                    _onExpirySettleError?.Invoke(closed, ex);
                }
            }
            // Stop on a short batch, and on one where nothing could be closed, so an article that stays Active
            // against the conditional close can never spin this loop (the next period retries it).
            if (due.Count < ExpirySweepBatch || closedThisBatch == 0) break;
        }
        foreach (var receiver in receivers)
            await _mail.NotifyNewMailAsync(receiver);
        return returned;
    }
}
