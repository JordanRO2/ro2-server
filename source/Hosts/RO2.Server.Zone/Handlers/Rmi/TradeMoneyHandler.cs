using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1355 ReqPCTradeMoney - Set trade gold amount</summary>
public class TradeMoneyHandler : ZoneRmiHandlerBase<TradeMoneyHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqPCTradeMoney;
    public override string Name => "TradeMoney";

    /// <summary>
    /// Bytes preceding the gold field in the C2S body: marker 0x01 (1) + compact length (1) + selfLen (2)
    /// + msgTag 58042 (2) + selfHandle (4).
    /// </summary>
    private const int GoldFieldOffset = 10;

    /// <summary>The gold amount is one 64-bit value (low dword then high dword).</summary>
    private const int GoldFieldLength = sizeof(ulong);

    /// <summary>Smallest valid body: the envelope header plus the 8-byte gold field (18 bytes).</summary>
    private const int MinBodyLength = GoldFieldOffset + GoldFieldLength;

    public TradeMoneyHandler(IServiceScopeFactory s, ILogger<TradeMoneyHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        // C2S body (compact-BA, marker 0x01, 18 B; MinBodyLength): contents = [u16 selfLen][u16 msgTag 58042]
        // [u32 selfHandle][u64 gold]. In PAYLOAD offsets the gold is the 8-byte field at body+10 (GoldFieldOffset): one 64-bit
        // amount stored as low and high dwords at blob+8 / blob+12. Stored as long; the offered amount is
        // validated at confirm time.
        if (payload.Length < MinBodyLength) return;
        var reader = new PacketReader(payload.Span);
        reader.Skip(GoldFieldOffset);
        // E-H10: the offer cannot exceed what the wallet holds now, and the echo carries the clamped amount. The
        // protocol defines no answer to an over-wallet offer, so the clamp
        // reuses the 2386 frame already rendered. The swap still re-checks affordability and debits.
        var gold = (long)Math.Min(reader.ReadUInt64(), (ulong)player.Gold);

        var trade = TradeSessionService.Instance.Get(player.SessionId);
        if (trade == null) return;

        // A LOCKED side's gold is frozen and nothing is answered: a 1355 sent after that side's own lock was
        // echoed (the duplicated 1355+1356 pair the client sends, a repeat after phase 1, one after the side's own
        // confirm) gets no 2386. Echoing them made both windows redraw an offer the
        // swap would not honour if it had changed.
        if (trade.SetGold(player.SessionId, gold) != TradeOfferChange.Applied)
        {
            Logger.LogDebug("{Name} changed trade gold on a locked or closing offer; no answer", player.Name);
            return;
        }
        var offered = trade.OfferFor(player.SessionId).Gold; // what the swap will actually move (clamped >= 0)
        Logger.LogInformation("{Name} sets trade gold to {Gold}", player.Name, offered);

        // 2386 OnDragGold to BOTH sides, carrying the offering PC's handle + the amount. The echo is the
        // full 64-bit value: the consumer reads blob+14 as one __int64 (see BuildOnDragGold) and amounts
        // above 2^32 are valid. Clamping it to u32, as this line used to, made
        // both windows show the low 32 bits of any larger offer while the swap moved the real amount.
        // An unchanged amount is echoed too: the client sends one right before every lock, and each is
        // answered with a 2386 immediately before the first 2387 of the round.
        var body = TradePackets.BuildOnDragGold(trade.HandleFor(player.SessionId), (ulong)offered);
        foreach (var sid in new[] { trade.SessionA, trade.SessionB })
        {
            var s = Broadcast.GetSession(sid);
            if (s != null)
                await s.SendRmiAsync(ZoneS2COpcodes.OnDragGold, body);
        }
    }
}
