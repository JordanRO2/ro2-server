using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Domain.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1401 ReqAuctionSellList - My auction sell listings</summary>
/// <remarks>
/// Request (blob 11, msgTag 58210): <c>[u32 requester handle @4][u16 page @8][u8 kind @10]</c>, kind 2 and a
/// page of 1 to 5 on every request. The client
/// sends it itself right after every 2458 (its consumer refreshes list 2), on opening Tab3 and on paging, so
/// this list is how a new consignment appears in the seller's window. Each one is answered with one 2455
/// at once.
/// </remarks>
public class AuctionSellListHandler : ZoneRmiHandlerBase<AuctionSellListHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqAuctionSellList;
    public override string Name => "AuctionSellList";
    public AuctionSellListHandler(IServiceScopeFactory s, ILogger<AuctionSellListHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (!AuctionPackets.TryParseListRequest(payload.Span, AuctionPackets.MsgTagReqSellList, out var page))
        {
            Logger.LogWarning("{Name} sent an unparseable ReqAuctionSellList", player.Name);
            return;
        }
        if (page == 0) page = 1;

        
        
        
        var auction = AuctionService.From(services, PlayerManager, Broadcast, Logger);
        var articles = await auction.GetSellerArticlesAsync(player.CharacterId);

        
        
        
        
        
        var store = services.GetRequiredService<GameDataStore>();
        var ordered = AuctionPackets.OrderForList(articles, store);

        
        
        
        var now = DateTime.UtcNow;
        var entries = new List<byte[]>();
        foreach (var a in ordered.Skip((page - 1) * AuctionPackets.PageSize).Take(AuctionPackets.PageSize))
            entries.Add(AuctionPackets.BuildEntry210(a, now, withSellerName: false, store));

        
        
        
        
        Logger.LogDebug("{Name} views sell list page {Page}: {Count} of {Total} articles", player.Name, page, entries.Count, articles.Count);
        await session.SendRmiAsync(ZoneS2COpcodes.OnAuctionSellList,
            AuctionPackets.BuildList(AuctionPackets.MsgTagSellList, player.EntityId, page, articles.Count, entries));
    }
}
