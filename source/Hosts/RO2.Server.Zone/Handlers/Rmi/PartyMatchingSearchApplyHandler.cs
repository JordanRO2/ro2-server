using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1460 ReqPartyMatchingSearchApply - list the party-matching apply board</summary>
/// <remarks>
/// The client sends it when party-matching window 41 opens or refreshes page 2, and by itself right after a
/// 2618 <c>[02 xx]</c> (the answer to its own 1458) or a 2620 kind 2 (the answer to its 1459 delete). Every
/// request is answered with a 2622.
/// </remarks>
public class PartyMatchingSearchApplyHandler : ZoneRmiHandlerBase<PartyMatchingSearchApplyHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqPartyMatchingSearchApply;
    public override string Name => "PartyMatchingSearchApply";

    public PartyMatchingSearchApplyHandler(IServiceScopeFactory s, ILogger<PartyMatchingSearchApplyHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        // Fixed 9 bytes (PartyApplyBoard.SearchBytes): [u8 category][u8 role flags][u8 difficulty][u16 page]
        // [u32 destination] (PartyApplyBoard.TryParseSearch). The default request has
        // every filter on "All", page 1.
        if (!PartyApplyBoard.TryParseSearch(payload.Span, out var filter)) return;

        Logger.LogDebug("Session {Id}: PartyMatchingSearchApply category={C} roles={R} difficulty={D} page={P} destination={Dest}",
            session.SessionId, filter.Category, filter.RoleFlags, filter.Difficulty, filter.Page, filter.Destination);

        // 2622 AnsPartyApplySearch: the notices 1458 posted (PartyApplyBoard), with each poster's name,
        // class and GID read now and absent posters dropped. With nothing posted
        // BuildAnsPartyApplySearch writes the empty body 01 05 01 00 01 00 00 (page 1 of 1, 0 rows).
        var rows = PartyApplyBoard.Instance.ResolveListings(PlayerManager.GetPlayerByCharacterId, filter);
        var (page, pages, pageRows) = PartyApplyBoard.Page(rows, filter.Page);
        await session.SendInternalRmiAsync(ZoneS2COpcodes.AnsPartyApplySearch,
            PartyApplySearchPackets.BuildAnsPartyApplySearch(page, pages, pageRows), ct);
    }
}
