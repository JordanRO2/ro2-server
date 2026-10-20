using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>Handles ReqIllusionPyramidGetResult (empty body): returns the outcome of the last draw.</summary>
/// <remarks>
/// <para>
/// The reply, AnsIllusionPyramidGetResult (2908), is 12 bytes: <c>[u16 result][u8 0][u8 kind][u32 id][u8 count][u8 floor][u8 slot][u8 end | broadcast&lt;&lt;1]</c>. The reward is the held draw's catalog record: kind 1 with an ItemInfo id, or kind 0 with a Mileage_ID for a mileage prize. Result 29007 with an empty record means no draw is held.
/// </para>
/// <para>
/// The fields update the pyramid state, and the client shows the message for the reward, or for a non-zero result.
/// </para>
/// <para>
/// <see cref="IllusionPyramidService.GetResultAsync"/> does the work under the per-player lock. It reports the held reward, raises the top record to its floor, and settles a mileage end at once. An item result is settled by the finish request (1619).
/// </para>
/// </remarks>
public class IllusionPyramidGetResultHandler : ZoneRmiHandlerBase<IllusionPyramidGetResultHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqIllusionPyramidGetResult;
    public override string Name => "IllusionPyramidGetResult";

    public IllusionPyramidGetResultHandler(IServiceScopeFactory s, ILogger<IllusionPyramidGetResultHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
        => IllusionPyramidService.GetResultAsync(session, player, services, Logger, ct);
}
