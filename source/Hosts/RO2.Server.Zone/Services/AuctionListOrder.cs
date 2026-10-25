using RO2.Economy.Domain;
using RO2.Domain.Entities;
using RO2.Domain.GameData;

namespace RO2.Server.Zone.Services;

public static partial class AuctionPackets
{
 /// <summary>
 /// Orders auction articles for the seller's own list and the tender list. Apply this to the whole list before paging.
 /// </summary>
 /// <remarks>
 /// <para>
 /// The order is the default search order: display name (ordinal), then current price ascending, then instant buyout price ascending, then template id ascending, then expiry descending (most remaining time first), then article id, newest first.
 /// </para>
 /// <para>
 /// Every key comes from the stored article (<see cref="SearchKeyOf"/>). Paging takes its pages from the ordered list.
 /// </para>
 /// </remarks>
 /// <param name="articles">Articles to order.</param>
 /// <param name="store">Game data used to look up display names. May be null.</param>
    public static List<AuctionArticle> OrderForList(IEnumerable<AuctionArticle> articles, GameDataStore? store)
        => OrderForList(articles, id => AuctionItemCatalog.DisplayName(id, store));

 /// <summary>
 /// Orders auction articles as <see cref="OrderForList(IEnumerable{AuctionArticle}, GameDataStore?)"/>, using the given display-name lookup.
 /// </summary>
 /// <param name="articles">Articles to order.</param>
 /// <param name="displayName">Maps an item id to its display name.</param>
    public static List<AuctionArticle> OrderForList(IEnumerable<AuctionArticle> articles, Func<uint, string> displayName)
    {
        
        return articles.OrderBy(SearchKeyOf, new SearchKeyComparer(SearchOrder.Default, displayName)).ToList();
    }
}
