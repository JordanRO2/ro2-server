using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1500 ReqPartyFightingTrim: the party leader asks the party whether it is battle ready. Answered with
/// 2698 AnsPartyFightingTrim to the leader and 2699 NfyPartyFightingTrimStart to the other online members,
/// which opens the "Battle Ready" dialog on each; the check itself is <see cref="PartyReadyCheckService"/>.
/// </summary>
/// <remarks>
/// The request is empty (zero argument bytes), sent by the UI command 65676. The leader gets
/// 2698 with a 2-byte zero body, then 2697 after its members' votes, and no 2699; each member gets the
/// 2699 <c>[01 10][u16 16][u16 58432][u32 0][u64 leader GID]</c> naming that leader.
/// Only the leader may start one; no refusal code exists for anyone else, so none is sent.
/// </remarks>
public class PartyFightingTrimHandler : ZoneRmiHandlerBase<PartyFightingTrimHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqPartyFightingTrim;
    public override string Name => "PartyFightingTrim";

    public PartyFightingTrimHandler(IServiceScopeFactory s, ILogger<PartyFightingTrimHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        var partyService = services.GetRequiredService<PartyService>();
        var party = partyService.GetPlayerParty(session.SessionId);
        if (party == null || party.LeaderSessionId != session.SessionId)
        {
            Logger.LogDebug("{Name}: 1500 from a player who does not lead a party; not answered", player.Name);
            return;
        }

        await PartyReadyCheckService.StartAsync(partyService, Broadcast, party, player, session, Logger);
    }
}
