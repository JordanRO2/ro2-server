using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1356 ReqPCTradeLock - Lock trade (ready to trade)</summary>
public class TradeLockHandler : ZoneRmiHandlerBase<TradeLockHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqPCTradeLock;
    public override string Name => "TradeLock";
    public TradeLockHandler(IServiceScopeFactory s, ILogger<TradeLockHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        
        
        
        
        
        if (payload.Length < 11) return;
        var reader = new PacketReader(payload.Span);
        reader.Skip(10);
        var lockFlag = reader.ReadByte(); 

        Logger.LogInformation("{Name} {Action} trade", player.Name, lockFlag != 0 ? "locks" : "unlocks");

        var trade = TradeSessionService.Instance.Get(player.SessionId);
        if (trade == null) return;

        
        
        
        
        
        
        
        
        
        
        
        
        
        
        var result = trade.SetLocked(player.SessionId, lockFlag != 0);
        if (!result.Applied)
        {
            Logger.LogDebug("{Name} sent a trade lock toggle that changes nothing; no answer", player.Name);
            return;
        }

        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        var body = TradePackets.BuildOnLockTradePC(trade.HandleFor(player.SessionId), result.BothLocked, lockFlag != 0);
        foreach (var sid in new[] { trade.SessionA, trade.SessionB })
        {
            var s = Broadcast.GetSession(sid);
            if (s != null)
                await s.SendRmiAsync(ZoneS2COpcodes.OnLockTradePC, body);
        }
    }
}
