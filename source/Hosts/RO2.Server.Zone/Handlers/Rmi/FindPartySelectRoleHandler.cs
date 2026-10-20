using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// RMI 1568 ReqFindParty_Party_SelectRole: a party member's answer to the role check (window 46) that a 2822
/// opened, <c>[u8 role flags]</c>. Answered with 2834, plus the round's 2806 to every member when the role
/// is taken, or a 2831 to every member when its slot is already filled (<see cref="DungeonFinderPartyRound"/>).
/// </summary>
/// <remarks>
/// A role whose slot is free is answered with a 2834 success code and the round's 2806; a role whose slot is
/// already counted is answered with a 2834 refusal code (8) together with a 2831. A 1568 that finds no open
/// round (the window outlived it) is answered the refused way to the sender alone: the 2834 closes window 46
/// and the 2831 resets the rest of the finder UI. The 2834 body is a u16.
/// </remarks>
public class FindPartySelectRoleHandler : ZoneRmiHandlerBase<FindPartySelectRoleHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqFindParty_Party_SelectRole;
    public override string Name => "FindParty_Party_SelectRole";

    public FindPartySelectRoleHandler(IServiceScopeFactory s, ILogger<FindPartySelectRoleHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (!FindPartyRolePackets.TryParseSelectRole1568(payload.Span, out var roleFlags))
        {
            Logger.LogDebug("Session {Id}: malformed ReqFindParty_Party_SelectRole ({Len}B)", session.SessionId, payload.Length);
            return;
        }
        roleFlags &= DungeonFinderPackets.RoleTank | DungeonFinderPackets.RoleDealer | DungeonFinderPackets.RoleHealer;
        if (roleFlags == 0)
        {
            // The client refuses an empty role selection itself, so no answer is defined for it.
            Logger.LogDebug("Session {Id}: ReqFindParty_Party_SelectRole with no role", session.SessionId);
            return;
        }

        var parties = services.GetRequiredService<PartyService>();
        var outcome = DungeonFinderPartyRound.Shared.SelectRole(player, roleFlags,
            DungeonFinderPartyRound.SeatCheckFor(PlayerManager, parties));
        await DungeonFinderPartyRound.DeliverAsync(outcome.Sends, Broadcast.GetSession);
        Logger.LogInformation("{Name} answered the dungeon finder role check with roles {Flags}: {Status} (role {Role})",
            player.Name, roleFlags, outcome.Status, outcome.Role);
    }
}
