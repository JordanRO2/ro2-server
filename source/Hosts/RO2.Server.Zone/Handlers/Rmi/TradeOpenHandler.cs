using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1351 ReqPCTradeOpen - Open player-to-player trade</summary>
public class TradeOpenHandler : ZoneRmiHandlerBase<TradeOpenHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqPCTradeOpen;
    public override string Name => "TradeOpen";
    public TradeOpenHandler(IServiceScopeFactory s, ILogger<TradeOpenHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        
        
        
        if (payload.Length < 14) return;
        var reader = new PacketReader(payload.Span);
        reader.Skip(10);
        var targetHandle = reader.ReadUInt32(); 

        var trades = TradeSessionService.Instance;
        bool IsOnline(uint sid) => PlayerManager.GetPlayer(sid) != null;

        
        
        
        
        
        var target = TradeSessionService.ResolvePc(PlayerManager, targetHandle);
        if (target == null || target.SessionId == player.SessionId)
        {
            
            
            
            Logger.LogInformation("{Name} requested a trade with {Target:X8}, who is not another online player",
                player.Name, targetHandle);
            await session.SendRmiAsync(ZoneS2COpcodes.OnOpenTradePC,
                TradePackets.BuildOnOpenTradePC(targetHandle, TradePackets.OpenTradeRefusedUnavailable));
            return;
        }

        
        
        
        
        
        if (trades.GetLive(player.SessionId, IsOnline) != null || trades.GetLive(target.SessionId, IsOnline) != null)
        {
            Logger.LogInformation("{Name} requested a trade with {Target}, but one of them is already trading",
                player.Name, target.Name);
            await session.SendRmiAsync(ZoneS2COpcodes.OnOpenTradePC,
                TradePackets.BuildOnOpenTradePC(target.EntityId, TradePackets.OpenTradeRefusedBusy));
            return;
        }

        
        
        
        
        
        if (IllusionPyramidRunStore.RecentlyUsed(target, Environment.TickCount64))
        {
            Logger.LogInformation("{Name} requested a trade with {Target}, who is playing the Illusion Pyramid",
                player.Name, target.Name);
            await session.SendRmiAsync(ZoneS2COpcodes.OnOpenTradePC,
                TradePackets.BuildOnOpenTradePC(target.EntityId, TradePackets.OpenTradeRefusedUnavailable));
            return;
        }

        Logger.LogInformation("{Name} opens trade with {Target}", player.Name, target.Name);

        
        
        
        
        trades.Request(player.SessionId, target.SessionId, player.EntityId, target.EntityId, IsOnline);

        
        
        await session.SendRmiAsync(ZoneS2COpcodes.OnOpenTradePC,
            TradePackets.BuildOnOpenTradePC(target.EntityId, 0));

        
        
        
        
        
        
        
        
        
        
        var targetSession = Broadcast.GetSession(target.SessionId);
        if (targetSession != null)
        {
            await targetSession.SendRmiAsync(
                ZoneS2COpcodes.OnBeginTradePC,
                TradePackets.BuildOnBeginTradePC(player.EntityId));
        }
    }
}
