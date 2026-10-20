using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1658 (ReqTRoomJoin). The player's answer to a tournament-room match prompt: one u8, 1 to accept and 0 to
/// decline. This server does not run a tournament-room matchmaker, so no match is ever pending. A stray answer
/// receives NfyEntryPartyError (2969); see <see cref="EntryPartyErrorPackets"/>.
/// </summary>
public class TRoomJoinHandler : ZoneRmiHandlerBase<TRoomJoinHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqTRoomJoin;
    public override string Name => "TRoomJoin";
    public TRoomJoinHandler(IServiceScopeFactory s, ILogger<TRoomJoinHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length != 1)
        {
            Logger.LogWarning("{Name}: 1658 ReqTRoomJoin with {Len} bytes (retail: 1)", player.Name, payload.Length);
            return;
        }
        bool accept = payload.Span[0] != 0;
        var code = EntryPartyErrorPackets.StaleAnswerCode(accept);
        Logger.LogInformation("{Name} {Answer} a tournament-room match, but no match is pending (no matchmaker); 2969 code {Code}",
            player.Name, accept ? "accepts" : "declines", code);
        await session.SendRmiAsync(ZoneS2COpcodes.NfyEntryPartyError, EntryPartyErrorPackets.Build(code), ct);
    }
}
