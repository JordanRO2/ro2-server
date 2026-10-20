using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1352 ReqPCTradeOpenConfirm - Accept/reject trade request</summary>
public class TradeOpenConfirmHandler : ZoneRmiHandlerBase<TradeOpenConfirmHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqPCTradeOpenConfirm;
    public override string Name => "TradeOpenConfirm";
    public TradeOpenConfirmHandler(IServiceScopeFactory s, ILogger<TradeOpenConfirmHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        
        
        
        
        if (payload.Length < 15) return;
        var reader = new PacketReader(payload.Span);
        reader.Skip(10);
        var accepted = reader.ReadByte() != 0; 
        
        
        
        var answeredHandle = reader.ReadUInt32();

        Logger.LogInformation("{Name} {Action} trade", player.Name, accepted ? "accepts" : "rejects");

        var trades = TradeSessionService.Instance;
        bool IsOnline(uint sid) => PlayerManager.GetPlayer(sid) != null;

        var initiator = TradeSessionService.ResolvePc(PlayerManager, answeredHandle);
        var request = initiator == null ? null : trades.RequestFrom(initiator.SessionId);
        bool current = request != null && request.SessionB == player.SessionId && request.HandleA == answeredHandle;

        if (!accepted)
        {
            
            
            
            
            
            
            
            
            
            
            
            
            if (!current || !trades.WithdrawRequest(request!))
            {
                Logger.LogDebug("{Name} rejected a trade request with no matching live request; nothing to answer",
                    player.Name);
                return;
            }

            await SendToAsync(TradeNotifyNotifyPackets.BuildOnBeginTradePCNotify(
                request!.HandleA, request.HandleB, TradeNotifyNotifyPackets.RejectedResultCode),
                request.SessionA, request.SessionB);
            return;
        }

        
        
        
        
        
        
        
        
        
        
        var myLive = trades.GetLive(player.SessionId, IsOnline);
        if (myLive != null && initiator != null && myLive.Has(initiator.SessionId))
        {
            
            
            
            Logger.LogDebug("{Name} re-accepted the trade that is already open", player.Name);
            return;
        }
        bool initiatorBusy = initiator != null && trades.GetLive(initiator.SessionId, IsOnline) != null;
        bool selfBusy = myLive != null;
        if (current && !initiatorBusy && !selfBusy && trades.TryAccept(request!))
        {
            await SendToAsync(TradeNotifyNotifyPackets.BuildOnBeginTradePCNotify(request!.HandleA, request.HandleB),
                request.SessionA, request.SessionB);
            return;
        }

        
        
        
        
        
        
        
        
        
        
        
        
        ushort code = initiatorBusy || selfBusy
            ? TradeNotifyNotifyPackets.BusyResultCode
            : TradeNotifyNotifyPackets.StaleRequestResultCode;
        Logger.LogInformation("{Name} accepted a trade request from {Initiator:X8} that cannot open (code {Code})",
            player.Name, answeredHandle, code);

        var refused = TradeNotifyNotifyPackets.BuildOnBeginTradePCNotify(answeredHandle, player.EntityId, code);
        await session.SendRmiAsync(ZoneS2COpcodes.OnBeginTradePCNotify, refused);
        if (initiator != null && !initiatorBusy && initiator.SessionId != player.SessionId)
            await SendToAsync(refused, initiator.SessionId);
    }

    private async Task SendToAsync(byte[] body, params uint[] sessionIds)
    {
        foreach (var sid in sessionIds)
        {
            var s = Broadcast.GetSession(sid);
            if (s != null)
                await s.SendRmiAsync(ZoneS2COpcodes.OnBeginTradePCNotify, body);
        }
    }
}
