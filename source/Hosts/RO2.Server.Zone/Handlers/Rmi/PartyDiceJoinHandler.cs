using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1380 ReqPartyDiceJoin — a party member's answer to an open loot roll (2468).</summary>
/// <remarks>
/// <para>
/// Body = compact ByteArray, blob <c>[u16 19][u16 58257][u32 own handle][u16 @8][u8 choice @10][u64 item uid @11,
/// unaligned]</c> (body 21; <see cref="PartyDicePackets.TryParseDiceJoin1380"/>), sent
/// from the loot-roll window: its two roll buttons send choice 0 and 1, its close button and its 180,000 ms
/// auto-pass choice 2.
/// </para>
/// <para>
/// Every 1380 whose sender's session holds the answer is answered: choice 0 with a 2469 whose roll is in
/// 1001..2000, choice 1 with a roll in 1..1000, choice 2 with a 2470, each to the
/// whole party, and the last participant's answer with the award (PartyDiceService). A 1380 that names a
/// uid with no open roll gets no answer. It never sends 2788
/// NfyPartyDiceChattingCmd here, which is what an older version of this handler
/// broadcast with a server-side random number.
/// </para>
/// </remarks>
public class PartyDiceJoinHandler : ZoneRmiHandlerBase<PartyDiceJoinHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqPartyDiceJoin;
    public override string Name => "PartyDiceJoin";

    public PartyDiceJoinHandler(IServiceScopeFactory s, ILogger<PartyDiceJoinHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (!PartyDicePackets.TryParseDiceJoin1380(payload.Span, out var choice, out var itemUid))
        {
            Logger.LogDebug("Session {Id}: malformed ReqPartyDiceJoin ({Len}B)", session.SessionId, payload.Length);
            return;
        }

        await PartyDiceService.AnswerAsync(PartyDiceService.Deps.From(services, Logger), player, itemUid, choice);
    }
}
