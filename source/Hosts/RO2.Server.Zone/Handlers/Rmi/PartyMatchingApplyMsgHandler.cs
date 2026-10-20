using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1458 ReqPartyMatchingApplyMsg - post a notice to the party-matching apply board</summary>
/// <remarks>
/// <para>
/// The Apply dialog's OK sends it, and only with at least one role
/// ticked (string 10155 otherwise). The body is fixed 49 bytes, NONE family:
/// <c>[u8 category][u8 role flags][u8 difficulty][u32 destination][wchar_t[21] title]</c>
/// (<see cref="PartyApplyBoard.TryParseApplyMsg"/>).
/// </para>
/// <para>
/// Retail lists the posted notice on the apply board: 2618 <c>02 01</c>, then the client's own 1460, and the 2622 answering it
/// lists this notice. The listing keeps answering that session's 1460s.
/// </para>
/// <para>
/// Behaviour for a 1458 from a player who is in a party is unknown. The recruit board drops a non-leader's 1554
/// (PartyMatchingInviteMsgHandler),
/// but that is the other board and another request, so it is not applied here: every poster is listed.
/// </para>
/// </remarks>
public class PartyMatchingApplyMsgHandler : ZoneRmiHandlerBase<PartyMatchingApplyMsgHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqPartyMatchingApplyMsg;
    public override string Name => "PartyMatchingApplyMsg";

    public PartyMatchingApplyMsgHandler(IServiceScopeFactory s, ILogger<PartyMatchingApplyMsgHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        
        var posting = PartyApplyBoard.Instance.Post(player.CharacterId, payload.Span);
        if (posting == null)
        {
            
            
            
            Logger.LogWarning("Session {Id}: {Name} sent a {Len}-byte PartyMatchingApplyMsg that cannot be listed; dropped",
                session.SessionId, player.Name, payload.Length);
            return;
        }

        Logger.LogDebug("Session {Id}: PartyMatchingApplyMsg category={C} roles={R} difficulty={D} destination={Dest} title='{Title}'",
            session.SessionId, posting.Category, posting.RoleFlags, posting.Difficulty, posting.Destination, posting.Title);

        
        
        
        await session.SendInternalRmiAsync(ZoneS2COpcodes.AnsPartyMatchingMsg,
            PartyStatePackets.BuildAnsPartyMatchingMsg(result: 2, flag: 1), ct);
    }
}
