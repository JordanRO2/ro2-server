using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1462 ReqPartyMatchingWhisper: the Apply button of the recruit-notice member window (opened by
/// the 2623 PartyMatchingDetailHandler sends). The server passes the application to the notice's poster
/// as 2625 AnsPartyMatchingWhisper, whose confirm dialog lets the poster invite the applicant.
/// </summary>
/// <remarks>
/// The shapes of 1462 and 2625 come from the client's builder
/// and reader (<see cref="PartyMatchingWhisperPackets"/>). The request is 13 bytes, not empty as this
/// handler used to say, and 2625 is a raw 52-byte body, not a compact blob. The client prints its own
/// "You have apply in @1 party." and waits for nothing, so the applicant only hears back on a refusal.
/// </remarks>
public class PartyMatchingWhisperHandler : ZoneRmiHandlerBase<PartyMatchingWhisperHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqPartyMatchingWhisper;
    public override string Name => "PartyMatchingWhisper";

    public PartyMatchingWhisperHandler(IServiceScopeFactory s, ILogger<PartyMatchingWhisperHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (!PartyMatchingWhisperPackets.TryParseRequest(payload.Span, out var request)) return;

        
        
        if (request.Roles == 0)
        {
            Logger.LogDebug("Session {Id}: PartyMatchingWhisper without a role, dropped", session.SessionId);
            return;
        }

        
        var poster = PartyMatchingDetailPackets.FindPoster(PlayerManager.GetAllPlayers(),
            PartyMatchingBoard.Instance.Snapshot(), request.PosterGid);
        if (poster == null)
        {
            Logger.LogDebug("Session {Id}: PartyMatchingWhisper to GID {Gid}: no such notice", session.SessionId, request.PosterGid);
            await RefuseAsync(session, PartyMatchingDetailPackets.CodeNoListing, ct);
            return;
        }

        
        if (poster.SessionId == player.SessionId) return;

        var parties = services.GetRequiredService<PartyService>();
        var code = PartyMatchingWhisperPackets.RefusalCode(parties.GetPlayerParty(player.SessionId),
            parties.GetPlayerParty(poster.SessionId));
        if (code != 0)
        {
            Logger.LogDebug("Session {Id}: PartyMatchingWhisper to {Poster} refused with {Code}: applicant already in a party",
                session.SessionId, poster.Name, code);
            await RefuseAsync(session, code, ct);
            return;
        }

        var posterSession = Broadcast.GetSession(poster.SessionId);
        if (posterSession == null) return;

        Logger.LogDebug("Session {Id}: PartyMatchingWhisper {Applicant} -> {Poster} roles {Roles}",
            session.SessionId, player.Name, poster.Name, request.Roles);
        await posterSession.SendRmiAsync(ZoneS2COpcodes.AnsPartyMatchingWhisper,
            PartyMatchingWhisperPackets.BuildAnsPartyMatchingWhisper(PartyMatchingDetailPackets.MemberOf(player), request.Roles), ct);
    }

 /// <summary>2624 [7][code] to the applicant (op 7 selects the apply texts of the 2624 consumer).</summary>
    private static Task RefuseAsync(ProudSession session, ushort code, CancellationToken ct)
        => session.SendRmiAsync(ZoneS2COpcodes.AnsPartyMatchingError,
            PartyMatchingDetailPackets.BuildError(PartyMatchingDetailPackets.ErrorOpApply, code), ct);
}
