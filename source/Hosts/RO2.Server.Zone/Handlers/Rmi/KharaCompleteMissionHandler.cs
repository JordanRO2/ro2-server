using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Network.Protocol;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1437 ReqKharaCompleteMission: completes a Khara and claims its reward.</summary>
/// <remarks>
/// <para>
/// The client builder for this request has no callers. Khara completion is server-driven:
/// <see cref="KharaService"/> completes a Khara as soon as its last condition holds, on accept, kill, loot and quest triggers.
/// </para>
/// <para>
/// A request completes the Khara only when it is accepted and every condition holds, using the same check and frames
/// as a server-driven completion. Otherwise nothing is sent.
/// </para>
/// </remarks>
public class KharaCompleteMissionHandler : ZoneRmiHandlerBase<KharaCompleteMissionHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqKharaCompleteMission;
    public override string Name => "KharaCompleteMission";
    public KharaCompleteMissionHandler(IServiceScopeFactory s, ILogger<KharaCompleteMissionHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        
        if (payload.Length < 4) return;
        var kharaId = new PacketReader(payload.Span).ReadUInt32();
        Logger.LogInformation("{Name} asks to complete Khara mission {KharaId}", player.Name, kharaId);

        await KharaService.RequestCompletionAsync(session, player, kharaId, services, Logger, ct);
    }
}
