using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1554 ReqPartyMatchingInviteMsg - post a recruit notice to the party-matching board</summary>
/// <remarks>
/// <para>
/// Every request is 48 bytes with the title "WL Open 1..." at +6, which confirms the builder's layout
/// (see HandleAsync).
/// </para>
/// <list type="bullet">
/// <item>A poster in no party is answered with 2618 <c>01 01</c>, and the client's 1560 then returns a 2621
/// holding the new row (one member, no party handle, the poster's name and the title).</item>
/// <item>A party member who is not its leader is not: the notice is never posted, no 2618 or 2624 is sent,
/// and the board rows stay unchanged (three foreign rows). So such a 1554 is dropped: nothing is posted and
/// nothing is answered.</item>
/// <item>A party leader posting keeps posting here, because 2621 rows carry a
/// 0x15 party handle and a member count above 1 (3 members, see
/// PartyMatchingBoard2621Tests), so notices from a party exist.</item>
/// </list>
/// </remarks>
public class PartyMatchingInviteMsgHandler : ZoneRmiHandlerBase<PartyMatchingInviteMsgHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqPartyMatchingInviteMsg;
    public override string Name => "PartyMatchingInviteMsg";

    /// <summary>
    /// 2618 <c>result</c> byte that makes the client reload the recruit-notice list (it re-sends 1560, answered by
    /// 2621). 2 would reload the listings list instead (1460, answered by 2622).
    /// </summary>
    private const byte AnsResultReloadNotices = 1;

    /// <summary>2618 <c>flag</c> byte. The client never reads it.</summary>
    private const byte AnsFlag = 1;

    public PartyMatchingInviteMsgHandler(IServiceScopeFactory s, ILogger<PartyMatchingInviteMsgHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        // A non-leader member's notice gets no posting and no answer (see the class remarks).
        var party = services.GetRequiredService<PartyService>().GetPlayerParty(session.SessionId);
        if (party is not null && party.LeaderSessionId != session.SessionId)
        {
            Logger.LogInformation("{Name} posted a party-matching notice as a non-leader member of party {Party}; dropped, as retail does",
                player.Name, party.PartyId);
            return;
        }

        // The request is a fixed 48 bytes: [u8 category][u8 difficulty][u32 destination][wchar_t[21] title]
        // (PartyMatchingBoard.TryParseInviteMsg), so the title starts at +6.
        //
        // Posting it is what makes 1560 return a populated 2621: that list is exactly these notices (every field
        // the 1554 carries reappears in the 2621 row), and PartyMatchingSearchInviteHandler builds it from this board.
        var posting = PartyMatchingBoard.Instance.Post(player.CharacterId, payload.Span);
        if (posting == null) return;

        Logger.LogDebug("Session {Id}: PartyMatchingInviteMsg category={C} difficulty={D} destination={Dest} title='{Title}'",
            session.SessionId, posting.Category, posting.Difficulty, posting.Destination, posting.Title);

        // 2618 AnsPartyMatchingMsg [u8 result][u8 flag]. The client reads only the first byte, and it picks which
        // party-matching list is reloaded: 1 re-requests the recruit notices (1560 -> 2621), which is the one
        // that shows the notice just posted; 2 re-requests the listings (1460 -> 2622).
        await session.SendRmiAsync(ZoneS2COpcodes.AnsPartyMatchingMsg,
            writer => writer.WriteBytes(PartyStatePackets.BuildAnsPartyMatchingMsg(result: AnsResultReloadNotices, flag: AnsFlag)));
    }
}
