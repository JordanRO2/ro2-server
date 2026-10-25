using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1400 ReqAuctionArticleList - Search auction listings</summary>
/// <remarks>
/// <para>
/// Request, U32 family: <c>[u32 len][u16 innerSize=37 (a constant, not a length)][u16 msgTag 57184]
/// [u32 requester handle][u16 page][9 filter bytes][u16 n][n × u32 id][u8 order][u8]</c>; the filters are
/// documented on <see cref="AuctionPackets.TryParseSearch"/>. The requests carry the requester's own 0x11 handle
/// at blob+4 and page 1 at blob+8.
/// </para>
/// <para>
/// This search is the only way a buyer learns another player's article uid, so it is what makes 1406
/// (and the 2693 type 1/2 notices it sends) reachable. It lists the articles every seller stored through
/// 1403 (<see cref="AuctionService.SearchAsync"/>), each with the seller's name filled in, as retail's
/// search records are and the seller's own lists are not.
/// </para>
/// <para>
/// Filters: High_Category, Medium_Category and Low_Category (each ignored at 0 / 0xFF), the name search's
/// id list, and the class checkbox at blob+14 (Require_Job 0 or bit job-1). Order: the
/// request's order byte decoded as retail's bit field (<see cref="AuctionPackets.SearchKeyComparer"/>):
/// bits 5-7 pick the column (name, level, +134, +142, remaining minutes) and each column's own bit its
/// direction. The whole
/// matching set is sorted before the page is cut, because the key decides page membership.
/// </para>
/// <para>
/// blob+12 / blob+14 of the answer count this page's records the requester is the top bidder of / is
/// selling, by character id (+202 / +194) (see <see cref="AuctionPackets.BuildSearch2456"/>). entry+202 is the top
/// bidder's uid and entry+134 the current bid (AuctionPackets.BuildEntry210).
/// </para>
/// </remarks>
public class AuctionArticleListHandler : ZoneRmiHandlerBase<AuctionArticleListHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqAuctionArticleList;
    public override string Name => "AuctionArticleList";

    /// <summary>Search pages are 1-based; a request for page 0 is answered as page 1.</summary>
    private const ushort FirstPage = 1;

 /// <remarks>
 /// RMI handlers are singletons built once at boot (WireZoneHandlers), so this starts reading the
 /// display-name / Low_Category tables the search sorts and filters on (36 MB of ItemInfo.json) in the
 /// background, instead of inside the first player's search.
 /// </remarks>
    public AuctionArticleListHandler(IServiceScopeFactory s, ILogger<AuctionArticleListHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc)
    {
        _ = Task.Run(AuctionItemCatalog.Warm);
    }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (!AuctionPackets.TryParseSearch(payload.Span, out var req))
        {
            Logger.LogWarning("{Name} sent an unparseable ReqAuctionArticleList ({Len} bytes)", player.Name, payload.Length);
            return;
        }
        ushort page = req.Page == 0 ? FirstPage : req.Page;

        var store = services.GetRequiredService<GameDataStore>();
        var auction = AuctionService.From(services, PlayerManager, Broadcast, Logger);
        var (total, articles) = await auction.SearchAsync(req, store);

        // The store resolves each article's rune-hole block, which the 2456 records carry
        // (see AuctionPackets.BuildEntry210).
        var now = DateTime.UtcNow;
        var entries = new List<byte[]>(articles.Count);
        foreach (var a in articles)
            entries.Add(AuctionPackets.BuildEntry210(a, now, withSellerName: true, store));

        // OnAuctionSearch (2456): handle and page echo the request, total matches @10, this page's record
        // count @16 (min(10, total - 10 * (page - 1))), then the 210-byte records @18. An empty store gives
        // the empty page.
        // @12 / @14: of THIS page's records (counted after the page is cut), those the requester is the top
        // bidder of and those it is selling, the identities BuildEntry210 writes at +202 and +194.
        var (ownTopBids, ownArticles) = AuctionPackets.OwnCounts(articles, player.CharacterId);
        Logger.LogDebug("{Name} searches auction page {Page} (category {High}/{Medium}/{Low}, job {Job}, {Named} named ids, order 0x{Order:X2}): {Count} of {Total}, {Own} own, {Bids} own top bids",
            player.Name, page, req.HighCategory, req.MediumCategory, req.LowCategory, req.JobFilter, req.ItemIds?.Length ?? 0, (byte)req.Order, entries.Count, total, ownArticles, ownTopBids);
        await session.SendRmiAsync(ZoneS2COpcodes.OnAuctionSearch,
            AuctionPackets.BuildSearch2456(player.EntityId, page, total, entries, ownTopBids, ownArticles));
    }
}
