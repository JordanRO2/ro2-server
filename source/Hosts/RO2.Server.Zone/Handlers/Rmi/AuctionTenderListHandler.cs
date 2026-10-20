using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Domain.GameData;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1402 ReqAuctionTenderList - the articles the player holds the top bid on (auction Tab2)</summary>
/// <remarks>
/// Request (blob 11, msgTag 58212): <c>[u32 requester handle @4][u16 page @8][u8 kind @10]</c>, the same
/// shape as 1401: page starts at 1; kind is 2 (a kind of 3 is answered with the same
/// empty page, so the byte stays unparsed). The client sends it when auction Tab2 opens or pages, and each one
/// is answered with one 2457 at once (handle and page echoed).
/// </remarks>
public class AuctionTenderListHandler : ZoneRmiHandlerBase<AuctionTenderListHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqAuctionTenderList;
    public override string Name => "AuctionTenderList";
    public AuctionTenderListHandler(IServiceScopeFactory s, ILogger<AuctionTenderListHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (!AuctionPackets.TryParseListRequest(payload.Span, AuctionPackets.MsgTagReqTenderList, out var page))
        {
            Logger.LogWarning("{Name} sent an unparseable ReqAuctionTenderList", player.Name);
            return;
        }
        if (page == 0) page = 1;

        var store = services.GetRequiredService<GameDataStore>();

        // The live articles this player is top bidder on (placed with 1407 bids): auction Tab2 lists them, and an
        // outbid, sold, cancelled or expired article drops out at the next 1402 (no 2457 is sent unprompted).
        // The total in the frame counts all of them, not just the current page.
        var auction = AuctionService.From(services, PlayerManager, Broadcast, Logger);
        var articles = await auction.GetTopBidsAsync(player.CharacterId);

        // Same order as the sibling list 2455 (see AuctionPackets.OrderForList): display name, then the two
        // price fields ascending, then most remaining time, then newest, over the whole list before the page is cut.
        var ordered = AuctionPackets.OrderForList(articles, store);

        // Each row is a 210-byte entry built like the public search rows (current bid, the player's own uid,
        // the seller's name), since the client renders all three auction lists with the same row widget.
        var now = DateTime.UtcNow;
        var entries = new List<byte[]>();
        foreach (var a in ordered.Skip((page - 1) * AuctionPackets.PageSize).Take(AuctionPackets.PageSize))
            entries.Add(AuctionPackets.BuildEntry210(a, now, withSellerName: true, store));

        // OnAuctionTenderList (2457): the compact-length list frame 2455 uses, msgTag 58213, handle and page
        // echoed, total @10, count @12, then the records. With no bids it is the empty page.
        Logger.LogDebug("{Name} views tender list page {Page}: {Count} of {Total} top bids", player.Name, page, entries.Count, articles.Count);
        await session.SendRmiAsync(ZoneS2COpcodes.OnAuctionTenderList,
            AuctionPackets.BuildList(AuctionPackets.MsgTagTenderList, player.EntityId, page, articles.Count, entries));
    }
}
