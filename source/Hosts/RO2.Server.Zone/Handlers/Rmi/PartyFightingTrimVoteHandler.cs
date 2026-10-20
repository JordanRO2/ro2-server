using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1501 ReqPartyFightingTrimVote: a member's answer to the "Battle Ready" dialog that 2699 opened, 1 for
/// OK and 0 for No. Answered with 2698 AnsPartyFightingTrim [0] (2700 15725 for a second vote); when the last
/// member who can answer has, the initiator gets 2697 NfyPartyFightingTrimResult
/// (<see cref="PartyReadyCheckService"/>).
/// </summary>
/// <remarks>
/// Body, NONE family, fixed 1 byte: <c>[u8 vote]</c> (sent by the party confirm dialog with 1 for callback
/// code 4097 and 0 for 4098). The answer is 2698 0000 a fraction of a second later; a 2700 is sent only for a
/// repeated vote, and the voting members do not receive the 2697.
/// </remarks>
public class PartyFightingTrimVoteHandler : ZoneRmiHandlerBase<PartyFightingTrimVoteHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqPartyFightingTrimVote;
    public override string Name => "PartyFightingTrimVote";

    public PartyFightingTrimVoteHandler(IServiceScopeFactory s, ILogger<PartyFightingTrimVoteHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length != 1)
        {
            Logger.LogWarning("{Name}: 1501 body is {Len} bytes (the client writes 1)", player.Name, payload.Length);
            return;
        }

        var partyService = services.GetRequiredService<PartyService>();
        var party = partyService.GetPlayerParty(session.SessionId);
        if (party == null) return;

        await PartyReadyCheckService.VoteAsync(partyService, party, player, session, payload.Span[0], Logger);
    }
}
