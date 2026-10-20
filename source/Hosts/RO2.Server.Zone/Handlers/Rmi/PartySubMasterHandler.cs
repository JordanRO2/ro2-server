using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1503 ReqPartySubMaster - promote a raid member to secondary raid leader</summary>
/// <remarks>
/// <para>
/// Sent from the party member popup menu (command 65678 "Promote to Secondary
/// Raid Leader", string 9423) for a member who is not already a sub-master; 65679 (1505) is shown for one who is.
/// The request carries the menu item's 8-byte GID.
/// </para>
/// <para>
/// Body: NONE family, fixed 8 bytes, the target's character GID, the
/// GID the roster records carry. The target appears in a sub-master slot of the session's next roster header.
/// Only a leader can send it: the client builds this member menu only when its roster's leader GID is its own,
/// and requesters go on to send the leader-only 1387 shortly after.
/// PartyService.SetSubMasterAsync decides and answers (2702 + 2703).
/// </para>
/// </remarks>
public class PartySubMasterHandler : ZoneRmiHandlerBase<PartySubMasterHandler>
{
    private const int BodyLength = 8;

    public override ushort Opcode => ZoneC2SOpcodes.ReqPartySubMaster;
    public override string Name => "PartySubMaster";

    public PartySubMasterHandler(IServiceScopeFactory s, ILogger<PartySubMasterHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < BodyLength)
        {
            Logger.LogDebug("Session {Id}: short ReqPartySubMaster ({Len}B)", session.SessionId, payload.Length);
            return;
        }
        var targetGid = BinaryPrimitives.ReadUInt64LittleEndian(payload.Span);

        var partyService = services.GetRequiredService<PartyService>();
        if (!await partyService.SetSubMasterAsync(session.SessionId, targetGid))
            Logger.LogDebug("{Name}: 1503 for GID {Gid} refused (not the leader, not a member, already one, or 3 already set)",
                player.Name, targetGid);
    }
}
