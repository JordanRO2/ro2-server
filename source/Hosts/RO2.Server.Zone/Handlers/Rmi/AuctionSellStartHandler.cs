using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Inventory.Domain;
using RO2.Characters.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1403 ReqAuctionSellStart - Post item for auction</summary>
/// <remarks>
/// Request layout, fee and rules are documented on <see cref="AuctionPackets.TryParseSellStart"/>,
/// <see cref="AuctionService.RegistrationFee"/> and <see cref="AuctionService.CheckSellStart"/>. The item leaves
/// the bag, the fee leaves the wallet, the article is stored, and 2458 tells the client all three. Every 1403
/// gets exactly one 2458, refusals included, the refused second copy of a double-sent 1403 included: the client sets the
/// auction manager's request-pending byte and only a 2454..2462 answer clears it, so an unanswered 1403 locks
/// search, paging, bids and buyouts.
/// </remarks>
public class AuctionSellStartHandler : ZoneRmiHandlerBase<AuctionSellStartHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqAuctionSellStart;
    public override string Name => "AuctionSellStart";
    public AuctionSellStartHandler(IServiceScopeFactory s, ILogger<AuctionSellStartHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc)
    {
        
        
        
        
        AuctionService.StartExpirySweep(s, pm, bc, l);
    }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (!AuctionPackets.TryParseSellStart(payload.Span, out var req))
        {
            
            
            Logger.LogWarning("{Name} sent an unparseable ReqAuctionSellStart", player.Name);
            await session.SendRmiAsync(ZoneS2COpcodes.OnAuctionSellStart,
                AuctionPackets.BuildSellStartRefusal2458(player.EntityId, AuctionPackets.ResultSellItemMismatch, player.Gold));
            return;
        }

        var auction = AuctionService.From(services, PlayerManager, Broadcast, Logger);
        var o = await auction.StartSaleAsync(player, req,
            services.GetRequiredService<IInventoryRepository>(),
            services.GetRequiredService<ICharacterRepository>(),
            services.GetRequiredService<GameDataStore>());

        
        
        
        if (o.Result != 0 || o.Article is null)
        {
            await session.SendRmiAsync(ZoneS2COpcodes.OnAuctionSellStart,
                AuctionPackets.BuildSellStartRefusal2458(player.EntityId,
                    o.Result != 0 ? o.Result : AuctionPackets.ResultSellInternalError, o.Wallet));
            return;
        }

        
        
        
        
        
        var entry = AuctionPackets.BuildEntry210(o.Article, o.Article.StartedAt, withSellerName: false,
            services.GetRequiredService<GameDataStore>());
        await session.SendRmiAsync(ZoneS2COpcodes.OnAuctionSellStart,
            AuctionPackets.BuildSellStart2458(player.EntityId, entry, o.Wallet, o.BagItemUid, o.BagSlot, o.CountLeft));
    }
}
