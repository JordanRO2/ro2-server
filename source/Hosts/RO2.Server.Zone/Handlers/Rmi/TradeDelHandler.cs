using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1354 ReqPCTradeDel - Remove item from trade window</summary>
public class TradeDelHandler : ZoneRmiHandlerBase<TradeDelHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqPCTradeDel;
    public override string Name => "TradeDel";
    public TradeDelHandler(IServiceScopeFactory s, ILogger<TradeDelHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        // Same C2S shape as 1353 (compact-BA, 24 B): item instance uid is the u64 at body+10 (msgTag 58040).
        if (payload.Length < 18) return;
        var reader = new PacketReader(payload.Span);
        reader.Skip(10);
        var itemUid = reader.ReadUInt64();

        var trade = TradeSessionService.Instance.Get(player.SessionId);
        if (trade == null) return;

        bool removed = trade.RemoveItem(player.SessionId, itemUid);
        Logger.LogInformation("{Name} withdraws item uid {Uid} from trade (on the table: {Removed})", player.Name, itemUid, removed);

        // 2385 OnDelTradePC to BOTH sides. Include the item record when the uid really was on THIS side's table and
        // the row is still this player's (so the client knows WHICH cell to clear); a zero record is length-exact
        // and crash-safe otherwise. The uid is client-supplied: resolving it unchecked sent any player's refine,
        // cards and dyes to both windows, an oracle readable by walking the sequential uids (E-H9).
        var record = new byte[126];
        if (removed)
        {
            var inventory = services.GetRequiredService<IInventoryRepository>();
            var item = await inventory.GetByIdAsync(itemUid);
            if (item is not null && item.CharacterId == player.CharacterId)
                record = WorldEntryService.BuildItemRecord126(item);
        }
        var body = TradePackets.BuildOnDelTradePC(trade.HandleFor(player.SessionId), record);
        foreach (var sid in new[] { trade.SessionA, trade.SessionB })
        {
            var s = Broadcast.GetSession(sid);
            if (s != null)
                await s.SendRmiAsync(ZoneS2COpcodes.OnDelTradePC, body);
        }
    }
}
