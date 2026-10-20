using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1461 ReqPartyMatchingDetail: the Member button of the party-recruit list, asking who is in the
/// notice's party. Answered with 2623 AnsPartyMatchingDetail, which opens the member window the 1462
/// application is sent from (PartyMatchingWhisperHandler).
/// </summary>
/// <remarks>
/// The shapes of 1461, 2623 and 2624 come from the client's
/// builder and readers (<see cref="PartyMatchingDetailPackets"/>). This handler used to answer every
/// request with 2624 [1][107], which the client shows as 9200 "Keyword must be between min. 2 characters
/// and max. 15 characters.", and the member window could never open.
/// </remarks>
public class PartyMatchingDetailHandler : ZoneRmiHandlerBase<PartyMatchingDetailHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqPartyMatchingDetail;
    public override string Name => "PartyMatchingDetail";

    public PartyMatchingDetailHandler(IServiceScopeFactory s, ILogger<PartyMatchingDetailHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        
        
        if (!PartyMatchingDetailPackets.TryParseRequest(payload.Span, out var rowHandle, out var posterGid))
            return;

        var poster = PartyMatchingDetailPackets.FindPoster(PlayerManager.GetAllPlayers(),
            PartyMatchingBoard.Instance.Snapshot(), posterGid);
        if (poster == null)
        {
            
            
            Logger.LogDebug("Session {Id}: PartyMatchingDetail for GID {Gid}: no such notice", session.SessionId, posterGid);
            await session.SendRmiAsync(ZoneS2COpcodes.AnsPartyMatchingError,
                PartyMatchingDetailPackets.BuildError(PartyMatchingDetailPackets.ErrorOpDetail,
                    PartyMatchingDetailPackets.CodeNoListing), ct);
            return;
        }

        
        
        var parties = services.GetRequiredService<PartyService>();
        var party = parties.GetPlayerParty(poster.SessionId);
        
        
        var members = PartyMatchingDetailPackets.DetailMembers(poster, party,
                memberId => party is { } p ? parties.GetOnlineMember(p, memberId) ?? p.Members.GetValueOrDefault(memberId) : null)
            .Select(PartyMatchingDetailPackets.MemberOf)
            .ToList();
        var partyHandle = party == null ? 0u : PartyAnswerPackets.PartyHandle(party);

        Logger.LogDebug("Session {Id}: PartyMatchingDetail {Poster} (row handle {Row:X8}, party {Party:X8}): {Count} members",
            session.SessionId, poster.Name, rowHandle, partyHandle, members.Count);
        await session.SendRmiAsync(ZoneS2COpcodes.AnsPartyMatchingDetail,
            PartyMatchingDetailPackets.BuildAnsPartyMatchingDetail(partyHandle, PartyAnswerPackets.MemberGid(poster), members), ct);
    }
}
