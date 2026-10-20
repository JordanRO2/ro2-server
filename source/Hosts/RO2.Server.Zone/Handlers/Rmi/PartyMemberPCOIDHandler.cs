using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1539 ReqPartyMemberPCOID - Request party member object IDs</summary>
public class PartyMemberPCOIDHandler : ZoneRmiHandlerBase<PartyMemberPCOIDHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqPartyMemberPCOID;
    public override string Name => "PartyMemberPCOID";

    public PartyMemberPCOIDHandler(IServiceScopeFactory s, ILogger<PartyMemberPCOIDHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        
        
        
        
        
        
        
        
        
        
        var partyService = services.GetRequiredService<PartyService>();
        var party = partyService.GetPlayerParty(session.SessionId);
        if (party == null || party.MemberSessionIds.Count == 0)
            return;

        await session.SendInternalRmiAsync(ZoneS2COpcodes.OnPartyNotifyCreate,
            partyService.BuildRosterBody(party, create: true));
        await session.SendInternalRmiAsync(ZoneS2COpcodes.AnsPartyMemberPCOID,
            PartyStatePackets.BuildAnsPartyMemberPCOID(partyService.BuildMemberGidList(party)));
    }
}
