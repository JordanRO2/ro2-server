using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1404 ReqAuctionSellTenderPrice - the seller sells an article to its top bidder at the top bid,
/// answered by 2459 OnAuctionSellTenderPrice.</summary>
/// <remarks>
/// <para>
/// <b>Trigger.</b> Auction Tab3, the button at this+328 it refuses with 7972 "There are no bidders for the current item." when the row's +150 (the bidder
/// column of the seller's list, AuctionPackets.BuildEntry210) is empty, else confirms 8585 "Do you want to sell at
/// the bidding price?" and sends it. So the client sends 1404 only for a row with a bidder, which a 2455 carries once a 1407 bid landed;
/// </para>
/// <para>
/// <b>Request</b> (blob 24, msgTag 58216): <c>[u32 handle @4][u64 article uid @8]
/// [u64 item uid @16]</c> (<see cref="AuctionPackets.TryParseSellTenderPrice"/>). The sale is
/// <see cref="AuctionService.AcceptTopBidAsync"/>: the item to the bidder, the proceeds to the seller (both by
/// mail), 2693 type 3 to the seller and type 4 to the bidder, 2449 to both.
/// </para>
/// <para>
/// <b>Answer</b> (<see cref="AuctionPackets.BuildSellTenderPrice2459"/>, layout from the consumer alone):
/// the request sets the auction manager's pending flag, which the consumer clears, so every parseable request is
/// answered. Result 0 removes the row (list 2) and prints 1556 "Bidding price is complete."; 16308, the answer to
/// every refusal, re-requests the seller list (1401 page 1 kind 2) and shows 8605.
/// </para>
/// </remarks>
public class AuctionSellTenderPriceHandler : ZoneRmiHandlerBase<AuctionSellTenderPriceHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqAuctionSellTenderPrice;
    public override string Name => "AuctionSellTenderPrice";
    public AuctionSellTenderPriceHandler(IServiceScopeFactory s, ILogger<AuctionSellTenderPriceHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (!AuctionPackets.TryParseSellTenderPrice(payload.Span, out var articleUid, out var itemUid))
        {
            Logger.LogWarning("{Name} sent an unparseable ReqAuctionSellTenderPrice", player.Name);
            return;
        }

        var auction = AuctionService.From(services, PlayerManager, Broadcast, Logger);
        var sold = await auction.AcceptTopBidAsync(player, articleUid, itemUid);

        await session.SendRmiAsync(ZoneS2COpcodes.OnAuctionSellTenderPrice,
            AuctionPackets.BuildSellTenderPrice2459(player.EntityId,
                sold != null ? (ushort)0 : AuctionPackets.ResultArticleGone, articleUid, itemUid));
    }
}
