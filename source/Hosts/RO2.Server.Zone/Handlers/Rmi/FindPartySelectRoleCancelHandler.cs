using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1574 ReqFindParty_Party_SelectRole_Cancel: a party member backs out of the role check. Answered with
/// 2830, and the member's round ends for everyone with a 2831 (<see cref="DungeonFinderPartyRound"/>).
/// </summary>
/// <remarks>
/// <para>
/// Empty body. The client sends it from two places, both after a 2822: the Cancel
/// button of window 46, and the 2822 handler on its own when the receiver is inside a dungeon.
/// </para>
/// <para>
/// The order of replies is 2830, then the 2831 round end.
/// 2831's non-zero message is literally 10612 "Someone in the party either canceled Find Dungeon or did not
/// select a role.", and for a receiver inside a dungeon 11024 "Dungeon search cannot be done while inside the
/// dungeon...", which is the automatic cancel. The canceller gets it too: its
/// finder UI is in the searching state the round's 2806 put it in, and only the 2831 resets it.
/// </para>
/// <para>
/// The client's 2830 handler uses its u16 only for logging and hides window 46, so the value is not significant.
/// </para>
/// </remarks>
public class FindPartySelectRoleCancelHandler : ZoneRmiHandlerBase<FindPartySelectRoleCancelHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqFindParty_Party_SelectRole_Cancel;
    public override string Name => "FindParty_Party_SelectRole_Cancel";

    public FindPartySelectRoleCancelHandler(IServiceScopeFactory s, ILogger<FindPartySelectRoleCancelHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        var parties = services.GetRequiredService<PartyService>();
        var outcome = DungeonFinderPartyRound.Shared.Cancel(player, DungeonFinderPartyRound.SeatCheckFor(PlayerManager, parties));

        await session.SendInternalRmiAsync(ZoneS2COpcodes.AnsFindParty_Party_SelectRole_Cancel,
            PartyStatePackets.BuildAnsFindPartySelectRoleCancel());
        await DungeonFinderPartyRound.DeliverAsync(outcome.Sends, Broadcast.GetSession);
        Logger.LogInformation("{Name} cancelled the dungeon finder role check (round ended: {Ended})", player.Name, outcome.Ended);
    }
}
