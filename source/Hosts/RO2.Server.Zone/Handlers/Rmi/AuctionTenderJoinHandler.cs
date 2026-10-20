using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Network.Protocol;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1407 ReqAuctionTenderJoin - a bid on an auction article, answered by 2462 OnAuctionTenderJoin.</summary>
/// <remarks>
/// <para>
/// <b>Trigger.</b> Auction Tab1: the client runs its checks and confirms 1566
/// "Do you want to bid on the item?" before sending. The request sets the auction manager's
/// pending flag, which blocks every other auction action until the 2462 consumer clears it, so
/// every parseable request is answered, refusals included.
/// </para>
/// <para>
/// <b>Request</b> (blob 36, msgTag 58222):
/// <c>[u32 handle @4][u64 article uid @8][u64 item uid @16][u32 item template id @24][u64 bid @28]</c>
/// (<see cref="AuctionPackets.TryParseTenderJoin"/>). The rules are on <see cref="AuctionService.CheckBid"/>, the
/// bid, its escrow and the outbid refund on <see cref="AuctionService.PlaceBidAsync"/>.
/// </para>
/// <para>
/// <b>Answer</b> (<see cref="AuctionPackets.BuildTenderJoin2462"/>): every bid is answered with a 2462
/// with result 0, the request's handle, uids and bid echoed and the bidder's wallet after the bid
/// at @34; no 2435, 2449 or 2693 accompanies it, so the wallet rides @34 alone. On result 0
/// the consumer stores @34 as the wallet, writes @26 into the search row's +134, and when that reaches the row's
/// instant price removes the row and shows 1570 "You've successfully bought the item...", else prints 1550
/// "You've bid on the item.". The refusal codes are not in the corpora (7/7 answers are 0); each one sent is a
/// code the consumer maps to a bid text through Auction_ShowResultMessageByCode (16308 re-requests the
/// search list first).
/// </para>
/// </remarks>
public class AuctionTenderJoinHandler : ZoneRmiHandlerBase<AuctionTenderJoinHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqAuctionTenderJoin;
    public override string Name => "AuctionTenderJoin";
    public AuctionTenderJoinHandler(IServiceScopeFactory s, ILogger<AuctionTenderJoinHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (!AuctionPackets.TryParseTenderJoin(payload.Span, out var req))
        {
            Logger.LogWarning("{Name} sent an unparseable ReqAuctionTenderJoin", player.Name);
            return;
        }

        var auction = AuctionService.From(services, PlayerManager, Broadcast, Logger);
        ushort result = await auction.PlaceBidAsync(player, req, services.GetRequiredService<ICharacterRepository>());

        
        
        await session.SendRmiAsync(ZoneS2COpcodes.OnAuctionTenderJoin,
            AuctionPackets.BuildTenderJoin2462(player.EntityId, result, req.ArticleUid, req.ItemUid, req.Bid, player.Gold));
    }
}
