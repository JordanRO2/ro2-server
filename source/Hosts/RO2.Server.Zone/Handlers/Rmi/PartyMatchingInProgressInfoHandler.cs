using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1557 ReqPartyMatchingInProgressInfo - Check dungeon finder queue status</summary>
public class PartyMatchingInProgressInfoHandler : ZoneRmiHandlerBase<PartyMatchingInProgressInfoHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqPartyMatchingInProgressInfo;
    public override string Name => "PartyMatchingInProgressInfo";

    public PartyMatchingInProgressInfoHandler(IServiceScopeFactory s, ILogger<PartyMatchingInProgressInfoHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        
        
        
        
        
        
        
        
        
        
        await session.SendInternalRmiAsync(ZoneS2COpcodes.AnsPartyMatchingInProgressInfo, new byte[10]);
    }
}
