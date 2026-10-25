using System.Buffers.Binary;
using System.Linq;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1529 ReqPartyTacticsTarget - Set tactics target (mark enemy)</summary>
public class PartyTacticsTargetHandler : ZoneRmiHandlerBase<PartyTacticsTargetHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqPartyTacticsTarget;
    public override string Name => "PartyTacticsTarget";

    public PartyTacticsTargetHandler(IServiceScopeFactory s, ILogger<PartyTacticsTargetHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        
        
        
        
        var blob = payload.Span;
        if (!PartyAnswerPackets.TryReadEnvelope(blob, PartyAnswerPackets.MsgTagReqPartyTacticsTarget, 14, out _))
        {
            Logger.LogDebug("Session {Id}: malformed ReqPartyTacticsTarget ({Len}B)", session.SessionId, payload.Length);
            return;
        }
        var mark = BinaryPrimitives.ReadUInt16LittleEndian(blob[8..]);
        var targetHandle = BinaryPrimitives.ReadUInt32LittleEndian(blob[10..]);

        if (mark != 0)
        {
            var data = services.GetRequiredService<GameDataStore>();
            if (!data.TacticsTarget.Any(t => t.Tactics_List == mark))
            {
                Logger.LogDebug("{Name} tactics mark {Mark} is not in Tactics_Target", player.Name, mark);
                return;
            }
        }

        var partyService = services.GetRequiredService<PartyService>();
        var party = partyService.GetPlayerParty(session.SessionId);
        if (party == null) return;

        
        
        
        
        PartyTacticsMarks.Set(party, targetHandle, mark);

        
        
        
        
        
        
        var body = PartyAnswerPackets.BuildAnsPartyTacticsTarget2751(player.MapId, mark, targetHandle);
        foreach (var memberId in LiveRecipients(partyService, party))
        {
            var memberSession = Broadcast.GetSession(memberId);
            if (memberSession != null)
                await memberSession.SendInternalRmiAsync(ZoneS2COpcodes.AnsPartyTacticsTarget, body);
        }
    }

 /// <summary>
 /// Who gets the live 2751 answer to a 1529: the session of every party member that is in the world
 /// (<see cref="PartyService.GetOnlineMember"/>), in roster order, the requester included. No map and no
 /// raid-group filter.
 /// </summary>
 /// <remarks>
 /// <para>
 /// The requester: every 1529 is answered in the requester's own stream, a fraction of a second later,
 /// with the one-entry 2751. Mark 0, a clear, is answered the same way.
 /// </para>
 /// <para>
 /// Every other member, not only a 5-slot raid group: retail's answer to a mark reaches every other member of the party,
 /// which is more than the four a raid group holds besides the requester, so the group rule that used to be here is refuted.
 /// </para>
 /// <para>
 /// Every map: retail does not hold the copy back from a member on another map: members on other maps receive the
 /// one-entry 2751s too.
 /// </para>
 /// <para>
 /// Not modelled: retail's channels. A retail channel is another zone listener (2556 hands the client its port), so the
 /// members that receive a mark are those on the requester's channel, and no group rule is needed. This server runs one listener for every channel
 /// (ChannelMoveHandler reconnects the client to the same Zone port), so every online member is on the
 /// requester's channel.
 /// </para>
 /// </remarks>
    internal static List<uint> LiveRecipients(PartyService partyService, PartyState party)
    {
        var recipients = new List<uint>();
        foreach (var memberId in party.MemberSessionIds.ToArray())
            if (partyService.GetOnlineMember(party, memberId) != null)
                recipients.Add(memberId);
        return recipients;
    }
}
