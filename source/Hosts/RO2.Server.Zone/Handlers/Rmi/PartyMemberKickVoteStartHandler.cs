using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1572 ReqPartyMemberKickVoteStart: a member asks the party to vote another member out. Answered with
/// 2826 [0][0] to the initiator and 2829 [initiator GID][target GID] to the party, which opens the
/// Approve/Oppose window (window 44) on the other members; their clicks are 1573 (PartyKickVoteHandler).
/// </summary>
/// <remarks>
/// <para>
/// The layout is NONE family, 8 bytes <c>[u64 targetGid]</c>, the target member's character GID, as pushed by
/// both the invite dialog and the party-frame popup.
/// The old handler read a u32 at body+0 as a session id, which is the low half of the
/// GID, so the target never resolved, and it sent a 1-byte 2826 and an 8-byte 2829, both short reads.
/// </para>
/// <para>
/// A request that names no member, or the initiator's own GID, or comes from someone in no party, is
/// dropped with a log line and no answer: the 2826 consumer knows no "not a member" code, only
/// 0, 15736 (cooldown) and 15737 (underway).
/// </para>
/// <para>
/// What is missing: the vote outcome. The pass threshold, the vote lifetime and the cooldown seconds of
/// 15736 are in no data table (Config_Def has no kick or vote row), so 1573 is not tallied, 2828
/// NfyResultPartyKickVote is never sent and the member is never removed by a vote. Reachability: the client
/// offers "Vote Kick" (65661) instead of "Kick" only when a party flag is set, which is roster header
/// blob+33 (2357/2361). It is 0 in retail and BuildRosterHeader never sets it, so today the
/// client does not send 1572 to this server.
/// </para>
/// </remarks>
public class PartyMemberKickVoteStartHandler : ZoneRmiHandlerBase<PartyMemberKickVoteStartHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqPartyMemberKickVoteStart;
    public override string Name => "PartyMemberKickVoteStart";

 /// The fixed body length: one u64 (8 bytes).
    internal const int BodyLength = 8;

    public PartyMemberKickVoteStartHandler(IServiceScopeFactory s, ILogger<PartyMemberKickVoteStartHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (!TryParse(payload.Span, out var targetGid))
        {
            Logger.LogDebug("Session {Id}: PartyMemberKickVoteStart body is {Len} bytes, not the reader's {Want}",
                session.SessionId, payload.Length, BodyLength);
            return;
        }

        var partyService = services.GetRequiredService<PartyService>();
        var party = partyService.GetPlayerParty(session.SessionId);
        if (party == null)
        {
            Logger.LogDebug("{Name} kick vote: not in a party; dropped", player.Name);
            return;
        }

        var initiatorGid = PartyAnswerPackets.MemberGid(player);
        var targetId = partyService.FindMemberByGid(party, targetGid);
        if (targetId == 0 || targetGid == initiatorGid)
        {
            Logger.LogDebug("{Name} kick vote: GID {Gid:X16} is {What} of party {PartyId}; dropped",
                player.Name, targetGid, targetId == 0 ? "no member" : "the initiator", party.PartyId);
            return;
        }

        
        
        
        await session.SendInternalRmiAsync(ZoneS2COpcodes.AnsPartyMemberKickVoteStart,
            PartyKickVotePackets.BuildAnsKickVoteStart2826(PartyKickVotePackets.ResultAccepted, 0));
        await partyService.BroadcastBodyToPartyAsync(party, ZoneS2COpcodes.NfyPartyMemberKickVote,
            PartyKickVotePackets.BuildNfyKickVote2829(initiatorGid, targetGid));

        Logger.LogInformation("{Name} started a kick vote against {Gid:X16} in party {PartyId}; votes are not tallied (no kick-vote rule in the data)",
            player.Name, targetGid, party.PartyId);
    }

 /// <summary>Read a 1572 body: exactly 8 bytes, the target's character GID.</summary>
    internal static bool TryParse(ReadOnlySpan<byte> body, out ulong targetGid)
    {
        targetGid = 0;
        if (body.Length != BodyLength) return false;
        targetGid = BinaryPrimitives.ReadUInt64LittleEndian(body);
        return targetGid != 0;
    }
}
