using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1384 ReqPartyOut - Leave party</summary>
public class PartyLeaveHandler : ZoneRmiHandlerBase<PartyLeaveHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqPartyOut;
    public override string Name => "PartyLeave";

    public PartyLeaveHandler(IServiceScopeFactory s, ILogger<PartyLeaveHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        
        if (!PartyAnswerPackets.TryGetCompactBlob(payload.Span, out var blob)
            || !PartyAnswerPackets.TryReadEnvelope(blob, PartyAnswerPackets.MsgTagReqPartyOut, 8, out var handle))
        {
            Logger.LogDebug("Session {Id}: malformed ReqPartyOut ({Len}B)", session.SessionId, payload.Length);
            return;
        }

        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        var partyService = services.GetRequiredService<PartyService>();
        if (partyService.GetPlayerParty(session.SessionId) != null)
            await partyService.LeavePartyAsync(session.SessionId, PartyLeaveReason.Left);
        else
        {
            var left = await partyService.LeaveAsOfflineMemberAsync(player);
            Logger.LogDebug("{Name} sent ReqPartyOut while in no party; left {Count} party(ies) that still held the character, answering 2349 so the client resets its party",
                player.Name, left);
        }

        
        
        
        
        
        
        await session.SendInternalRmiAsync(ZoneS2COpcodes.OnPartyOut,
            PartyAnswerPackets.BuildResultAck(PartyStatePackets.MsgTagOnPartyOut, handle));
    }
}
