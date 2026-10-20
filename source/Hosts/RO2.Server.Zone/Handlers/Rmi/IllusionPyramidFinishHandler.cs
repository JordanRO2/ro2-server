using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles RMI 1619 ReqIllusionPyramidFinish: the player claims the prize slot or closes the pyramid window.</summary>
/// <remarks>
/// <para>
/// The request body is an empty wstring in a u32 envelope and carries nothing to act on.
/// </para>
/// <para>
/// The reply is 2909, <c>[u16 result][u8 closeFlag]</c>, preceded by the inventory updates of a settled item result. Result 0 means the run settled; 29004 means the item was sent to the mailbox; 29007 means no draw was accepted since the last finish.
/// </para>
/// <para>
/// <see cref="IllusionPyramidService.FinishAsync"/> does the work under the per-player lock: a held item result is settled (the entry is charged, the item goes to the bag) and the run is closed. The unsolicited 2909 replies with closeFlag 1 are sent when the run ends on departure, map load, death or combat (<see cref="IllusionPyramidService.CloseOnDepartureAsync"/>, <see cref="IllusionPyramidService.CloseOnMapLoadAsync"/>, <see cref="IllusionPyramidService.CloseOnDeathAsync"/>, <see cref="IllusionPyramidService.NotifyCombatAsync"/>).
/// </para>
/// </remarks>
public class IllusionPyramidFinishHandler : ZoneRmiHandlerBase<IllusionPyramidFinishHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqIllusionPyramidFinish;
    public override string Name => "IllusionPyramidFinish";

    public IllusionPyramidFinishHandler(IServiceScopeFactory s, ILogger<IllusionPyramidFinishHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
        => IllusionPyramidService.FinishAsync(session, player, services, Logger, ct);
}
