using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1573 ReqPartyKickVote: the Approve / Oppose click in the party kick-vote window (window 44). Answered
/// with a neutral 2827 AnsPartyKickVote, the one message that closes that window. No vote is tallied: the
/// kick-vote system does not exist on this server.
/// </summary>
/// <remarks>
/// <para>
/// Layout from the client alone. The request is NONE family, 1 byte <c>[u8 vote]</c>. The window's handler
/// sends it: its Approve button sends 1 and its Oppose button sends 0. The handler does not close the window itself.
/// </para>
/// <para>
/// 2827 is NONE family, one u16. The client only logs it ('%s - Error : %d') and hides window 44 whatever the
/// value. So 0 is sent: the consumer never branches on it, and it is the one value that is not an invented result code.
/// </para>
/// <para>
/// What is missing: a kick-vote system. The rule that decides a vote (its pass threshold) and the cooldown
/// seconds of string 10372 "This player can't be kicked for @1m. @2s." are in no data table, so any tally
/// would be invented. Window 44 is opened only by 2829 NfyPartyMemberKickVote (two u64 GIDs),
/// which 1572's handler now sends as [initiatorGid][targetGid] after its
/// 2826 [0][0]; the remaining gate is that the client offers Vote Kick only when roster header blob+33 is set,
/// which BuildRosterHeader never does. For whoever implements it: 2828's result 0 with the recipient's
/// own GID shows 10369 "You have been dropped from the party." and 1 shows 10370 "Kick Vote was thrown out.".
/// </para>
/// </remarks>
public class PartyKickVoteHandler : ZoneRmiHandlerBase<PartyKickVoteHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqPartyKickVote;
    public override string Name => "PartyKickVote";

 /// <summary>1573 vote byte for the window's Approve button.</summary>
    public const byte VoteApprove = 1;

    public PartyKickVoteHandler(IServiceScopeFactory s, ILogger<PartyKickVoteHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length != 1)
            Logger.LogDebug("Session {Id}: PartyKickVote body is {Len} bytes, not the reader's 1", session.SessionId, payload.Length);
        var vote = payload.Length >= 1 ? payload.Span[0] : (byte)0;

        Logger.LogInformation("{Name} voted {Vote} in a party kick vote; not tallied, this server has no kick-vote system",
            player.Name, vote == VoteApprove ? "approve" : "oppose");

        await session.SendInternalRmiAsync(ZoneS2COpcodes.AnsPartyKickVote, BuildAnsPartyKickVote2827());
    }

 /// <summary>
 /// 2827 AnsPartyKickVote: NONE family, <c>[u16 0]</c>. The client logs the value and closes
 /// window 44 for any value, so the neutral 0 is sent (see the class remarks).
 /// </summary>
    internal static byte[] BuildAnsPartyKickVote2827() => [0, 0];
}
