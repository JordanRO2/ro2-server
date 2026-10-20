using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1505 ReqPartyDismissSubMaster - take the secondary raid leader role back from a member</summary>
/// <remarks>
/// <para>
/// Body: NONE family, fixed 8 bytes, the member's character GID (read_side U64), sent from the party popup
/// menu, which only offers the action for a member that IsSubMaster reports. The GID is one a 1503 had
/// promoted. The answer is 2706 00 00 and 2707 naming that GID in the same millisecond, and the next
/// roster drops the member from its slot.
/// </para>
/// <para>
/// The old handler did not read the body (it was thought to be a name) and cleared a single sub-master
/// slot. The party now holds up to three, as the client does, so the GID picks which one.
/// PartyService.DismissSubMasterAsync decides and answers (2706 + 2707).
/// </para>
/// </remarks>
public class PartyDismissSubMasterHandler : ZoneRmiHandlerBase<PartyDismissSubMasterHandler>
{
    private const int BodyLength = 8;

    public override ushort Opcode => ZoneC2SOpcodes.ReqPartyDismissSubMaster;
    public override string Name => "PartyDismissSubMaster";

    public PartyDismissSubMasterHandler(IServiceScopeFactory s, ILogger<PartyDismissSubMasterHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < BodyLength)
        {
            Logger.LogDebug("Session {Id}: short ReqPartyDismissSubMaster ({Len}B)", session.SessionId, payload.Length);
            return;
        }
        var targetGid = BinaryPrimitives.ReadUInt64LittleEndian(payload.Span);

        var partyService = services.GetRequiredService<PartyService>();
        if (!await partyService.DismissSubMasterAsync(session.SessionId, targetGid))
            Logger.LogDebug("{Name}: 1505 for GID {Gid} refused (not the leader, or not one of the party's sub-masters)",
                player.Name, targetGid);
    }
}
