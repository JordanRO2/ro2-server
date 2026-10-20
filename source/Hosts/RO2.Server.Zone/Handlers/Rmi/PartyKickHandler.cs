using System.Buffers.Binary;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1388 ReqPartyExpulsion - Kick member from party</summary>
public class PartyKickHandler : ZoneRmiHandlerBase<PartyKickHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqPartyExpulsion;
    public override string Name => "PartyKick";

    public PartyKickHandler(IServiceScopeFactory s, ILogger<PartyKickHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        
        
        
        
        if (!PartyAnswerPackets.TryGetCompactBlob(payload.Span, out var blob)
            || !PartyAnswerPackets.TryReadEnvelope(blob, PartyAnswerPackets.MsgTagReqPartyExpulsion, 16, out var handle))
        {
            Logger.LogDebug("Session {Id}: malformed ReqPartyExpulsion ({Len}B)", session.SessionId, payload.Length);
            return;
        }
        var targetGid = BinaryPrimitives.ReadUInt64LittleEndian(blob[8..]);
        if (targetGid == 0) return;

        var partyService = services.GetRequiredService<PartyService>();
        var party = partyService.GetPlayerParty(session.SessionId);
        if (party == null || party.LeaderSessionId != session.SessionId) return;

        
        
        
        
        
        
        var targetSessionId = partyService.FindMemberByGid(party, targetGid);
        if (targetSessionId == 0)
        {
            
            
            
            
            
            
            Logger.LogDebug("{Name} kick: GID {Gid} names no member of party {PartyId}",
                player.Name, targetGid, party.PartyId);
            await session.SendInternalRmiAsync(ZoneS2COpcodes.OnPartyExpulsion,
                PartyAnswerPackets.BuildResultAck(PartyAnswerPackets.MsgTagOnPartyExpulsion, handle,
                    PartyAnswerPackets.ResultExpulsionNotMember));
            return;
        }
        if (targetSessionId == session.SessionId)
        {
            
            
            Logger.LogDebug("{Name} kick: GID {Gid} is the kicker itself", player.Name, targetGid);
            return;
        }
        var targetOnline = partyService.GetOnlineMember(party, targetSessionId) != null;

        
        
        
        
        
        
        
        
        
        if (party.MemberSessionIds.Count <= 2)
            await session.SendInternalRmiAsync(PartyStatePackets.OpcodeOnPartyNotifyDestroy,
                PartyStatePackets.BuildOnPartyNotifyDestroy2358(PartyAnswerPackets.PartyHandle(party)));

        
        
        
        await session.SendInternalRmiAsync(ZoneS2COpcodes.OnPartyExpulsion,
            PartyAnswerPackets.BuildResultAck(PartyAnswerPackets.MsgTagOnPartyExpulsion, handle));

        
        
        
        
        
        
        var notify = PartyAnswerPackets.BuildPartyNotifyExpulsion2366(PartyAnswerPackets.PartyHandle(party), targetGid);
        foreach (var memberId in party.MemberSessionIds.ToArray())
        {
            if (memberId == targetSessionId) continue;
            if (partyService.GetOnlineMember(party, memberId) == null) continue;
            var memberSession = Broadcast.GetSession(memberId);
            if (memberSession != null)
                await memberSession.SendInternalRmiAsync(ZoneS2COpcodes.OnPartyNotifyExpulsion, notify);
        }

        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        
        var targetSession = targetOnline ? Broadcast.GetSession(targetSessionId) : null;
        if (targetSession != null)
            await targetSession.SendInternalRmiAsync(ZoneS2COpcodes.OnPartyNotifyLayoff,
                PartyLayoffPackets.BuildPartyNotifyLayoff2367());

        
        
        
        await partyService.LeavePartyAsync(targetSessionId, PartyLeaveReason.Kicked);
    }
}
