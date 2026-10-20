using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1406 ReqAuctionBuySellingPrice - Buy at selling price (buyout)</summary>
/// <remarks>
/// Request (blob 36, msgTag 58220): <c>[u32 handle @4][u64 article uid @8][u64 item uid @16]
/// [u32 item template id @24][u64 price @28]</c>. Business rules are on <see cref="AuctionService.BuyAsync"/>.
/// </remarks>
public class AuctionBuySellingPriceHandler : ZoneRmiHandlerBase<AuctionBuySellingPriceHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqAuctionBuySellingPrice;
    public override string Name => "AuctionBuySellingPrice";
    public AuctionBuySellingPriceHandler(IServiceScopeFactory s, ILogger<AuctionBuySellingPriceHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (!AuctionPackets.TryParseBuy(payload.Span, out var req))
        {
            Logger.LogWarning("{Name} sent an unparseable ReqAuctionBuySellingPrice", player.Name);
            return;
        }

        var auction = AuctionService.From(services, PlayerManager, Broadcast, Logger);
        var (result, article) = await auction.BuyAsync(player, req, services.GetRequiredService<ICharacterRepository>());

        if (result != AuctionService.BuyResult.Bought || article == null)
        {
            // Every 1406 gets exactly one 2461: the client marks the auction request as pending and only an
            // answer clears it, so a silent refusal locks search, paging, bids and buyouts (see
            // AuctionSellStartHandler). The wallet is the unchanged one (== the last 2435) and the request's
            // article/item uids are echoed.
            //  - Gone (an article sold hours earlier): 16308.
            //  - NotEnoughMoney: 16302 (2114 "Not enough money in your inventory.").
            //  - Refused (own article, or no instant price): defensive. 16308 is the already-rendered
            //    "article is gone" text, chosen over an invented code.
            ushort code = result == AuctionService.BuyResult.NotEnoughMoney
                ? AuctionPackets.ResultNotEnoughMoney
                : AuctionPackets.ResultArticleGone;
            await session.SendRmiAsync(ZoneS2COpcodes.OnAuctionBuySellingPrice,
                AuctionPackets.BuildBuySellingPrice2461(player.EntityId, code, req.ArticleUid, req.ItemUid, player.Gold));
            return;
        }

        // Success is 2693 type 2, 2449, 2461 in that order: the notice (article
        // uid + item template id), the new-mail count (the item is in the mailbox), and 2461 with result 0,
        // the request's article/item uids and the buyer's wallet after paying the instant price.
        await session.SendRmiAsync(ZoneS2COpcodes.NfyAuctionSystemMessage,
            AuctionPackets.BuildSystemMessage2693(player.EntityId, AuctionPackets.NotifyBought, article.Uid, article.ItemId, 0));
        await auction.NotifyNewMailAsync(player.CharacterId);
        await session.SendRmiAsync(ZoneS2COpcodes.OnAuctionBuySellingPrice,
            AuctionPackets.BuildBuySellingPrice2461(player.EntityId, 0, req.ArticleUid, req.ItemUid, player.Gold));
    }
}
