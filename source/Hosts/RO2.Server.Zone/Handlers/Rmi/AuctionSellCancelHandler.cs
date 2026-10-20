using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1405 ReqAuctionSellCancle - Cancel auction listing</summary>
/// <remarks>
/// Request (blob 24, msgTag 58218): <c>[u32 handle @4][u64 article uid @8][u64 item uid @16]</c>.
/// </remarks>
public class AuctionSellCancelHandler : ZoneRmiHandlerBase<AuctionSellCancelHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqAuctionSellCancle;
    public override string Name => "AuctionSellCancel";
    public AuctionSellCancelHandler(IServiceScopeFactory s, ILogger<AuctionSellCancelHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (!AuctionPackets.TryParseCancel(payload.Span, out var articleUid, out var itemUid))
        {
            Logger.LogWarning("{Name} sent an unparseable ReqAuctionSellCancle", player.Name);
            return;
        }

        var auction = AuctionService.From(services, PlayerManager, Broadcast, Logger);
        var closed = await auction.CancelAsync(player, articleUid);

        if (closed == null)
        {
            
            
            
            Logger.LogInformation("{Name} cancels auction {Uid}: not an active article of theirs", player.Name, articleUid);
            await session.SendRmiAsync(ZoneS2COpcodes.OnAuctionSellCancel,
                AuctionPackets.BuildSellCancel2460(player.EntityId, AuctionPackets.ResultArticleGone, articleUid, itemUid));
            return;
        }

        
        
        
        
        await session.SendRmiAsync(ZoneS2COpcodes.NfyAuctionSystemMessage,
            AuctionPackets.BuildSystemMessage2693(player.EntityId, AuctionPackets.NotifyCancelled, closed.Uid, closed.ItemId, 0));
        await session.SendRmiAsync(ZoneS2COpcodes.OnAuctionSellCancel,
            AuctionPackets.BuildSellCancel2460(player.EntityId, 0, articleUid, itemUid));

        
        
        
        
        
        
        
    }
}
