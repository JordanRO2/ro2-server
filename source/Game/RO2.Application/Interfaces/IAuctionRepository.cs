using RO2.Domain.Entities;

namespace RO2.Application.Interfaces;

/// <summary>
/// Persistence for auction house articles and their id counter.
/// </summary>
/// <remarks>
/// Each state change is a single conditional update on the article's current state, so a double click or two
/// competing buyers can close an article only once. Methods named Try* return the updated article, or null when
/// the condition no longer holds. Packet building, search ordering, bid and listing rules, wallet escrow and mail
/// settlement stay in the Zone host. The bid flow is a read followed by a conditional update.
/// </remarks>
public interface IAuctionRepository
{
 /// <summary>Returns the next article id from the counter, creating the counter on first use.</summary>
    Task<ulong> GetNextIdAsync(CancellationToken ct = default);

    Task InsertAsync(AuctionArticle article, CancellationToken ct = default);

    Task<AuctionArticle?> GetByUidAsync(ulong uid, CancellationToken ct = default);

 /// <summary>The seller's live (active, unexpired) articles, newest first.</summary>
    Task<IReadOnlyList<AuctionArticle>> GetActiveBySellerAsync(uint sellerCharId, DateTime nowUtc, CancellationToken ct = default);

 /// <summary>How many live (active, unexpired) articles the seller has.</summary>
    Task<int> CountActiveBySellerAsync(uint sellerCharId, DateTime nowUtc, CancellationToken ct = default);

 /// <summary>
 /// Returns the sort fields of every live, unexpired article, optionally restricted to <paramref name="itemIds"/>.
 /// A null list means no filter; an empty list returns no rows.
 /// </summary>
 /// <remarks>
 /// Each article is a partial projection: only <c>Id</c>, <c>ItemId</c>, <c>StartPrice</c>, <c>BuyoutPrice</c>,
 /// <c>TopBid</c>, <c>TopBidderCharId</c> and <c>ExpiresAt</c> are loaded. The caller sorts them in memory and then
 /// loads the page with <see cref="GetByIdsAsync"/>.
 /// </remarks>
    Task<IReadOnlyList<AuctionArticle>> GetActiveSortRowsAsync(IReadOnlyCollection<uint>? itemIds, DateTime nowUtc, CancellationToken ct = default);

 /// <summary>Full documents for <paramref name="ids"/>, in no particular order (an id that is gone is simply absent).</summary>
    Task<IReadOnlyList<AuctionArticle>> GetByIdsAsync(IReadOnlyCollection<ulong> ids, CancellationToken ct = default);

 /// <summary>The distinct templates on sale right now, so a category filter becomes a short id list.</summary>
    Task<IReadOnlyList<uint>> GetActiveItemIdsAsync(DateTime nowUtc, CancellationToken ct = default);

 /// <summary>The seller's articles whose duration ran out but that were never closed.</summary>
    Task<IReadOnlyList<AuctionArticle>> GetDueBySellerAsync(uint sellerCharId, DateTime nowUtc, CancellationToken ct = default);

 /// <summary>Returns up to <paramref name="limit"/> overdue, unclosed articles from any seller, earliest expiry first.</summary>
    Task<IReadOnlyList<AuctionArticle>> GetDueAsync(DateTime nowUtc, int limit, CancellationToken ct = default);

 /// <summary>Cancels an active article owned by the seller. Returns null when it is no longer active.</summary>
    Task<AuctionArticle?> TryCancelAsync(ulong uid, uint sellerCharId, DateTime nowUtc, CancellationToken ct = default);

 /// <summary>Sells an active, unexpired article to the buyer. Returns null when it is no longer available.</summary>
    Task<AuctionArticle?> TrySellAsync(ulong uid, uint buyerCharId, DateTime nowUtc, CancellationToken ct = default);

 /// <summary>Marks an active article past its duration as expired. Returns null when it was closed another way.</summary>
    Task<AuctionArticle?> TryExpireAsync(ulong uid, DateTime nowUtc, CancellationToken ct = default);

 /// <summary>
 /// Makes the bidder the top bidder at <paramref name="bid"/>, if the article is still active, unexpired, and still
 /// carries the top bid the caller read (<paramref name="read"/>).
 /// </summary>
 /// <returns>The updated article, or null when the condition no longer holds.</returns>
    Task<AuctionArticle?> TryPlaceBidAsync(AuctionArticle read, uint bidderCharId, string bidderName,
        ulong bidderCharUid, long bid, DateTime nowUtc, CancellationToken ct = default);

 /// <summary>
 /// Sells the article to a bidder who reaches the instant-buy price at <paramref name="bid"/>. Uses the same
 /// condition as <see cref="TryPlaceBidAsync"/>.
 /// </summary>
    Task<AuctionArticle?> TryWinAtInstantPriceAsync(AuctionArticle read, uint bidderCharId, string bidderName,
        ulong bidderCharUid, long bid, DateTime nowUtc, CancellationToken ct = default);

 /// <summary>
 /// The seller accepts the top bid of <paramref name="read"/>. Sells the article to that bidder, under the same
 /// condition as <see cref="TryPlaceBidAsync"/>, and only if the article belongs to <paramref name="sellerCharId"/>.
 /// </summary>
    Task<AuctionArticle?> TryAcceptTopBidAsync(AuctionArticle read, uint sellerCharId, DateTime nowUtc, CancellationToken ct = default);

 /// <summary>The live, unexpired articles <paramref name="bidderCharId"/> is top bidder on, newest first.</summary>
    Task<IReadOnlyList<AuctionArticle>> GetTopBidsAsync(uint bidderCharId, DateTime nowUtc, CancellationToken ct = default);

 /// <summary>How many live articles <paramref name="bidderCharId"/> is top bidder on.</summary>
    Task<int> CountTopBidsAsync(uint bidderCharId, DateTime nowUtc, CancellationToken ct = default);
}
