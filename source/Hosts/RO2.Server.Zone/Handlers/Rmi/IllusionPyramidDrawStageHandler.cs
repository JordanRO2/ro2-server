using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// Handles RMI 1617 ReqIllusionPyramidDrawStage: draws one floor of the Illusion Pyramid. Answered with AnsIllusionPyramidDrawStage (2907).
/// </summary>
/// <remarks>
/// <para>
/// The request body is 7 bytes, without envelope, parsed by <see cref="IllusionPyramidService.TryParseDraw"/>. Nothing is charged at draw time: the entry is paid once, when the run settles (1618 / 1619).
/// </para>
/// <para>
/// The reply is <c>[u16 result][u8 0]</c> (<see cref="IllusionPyramidService.Build2907"/>). The result comes from the run kept by <see cref="IllusionPyramidService.DrawAsync"/>. The result codes are listed in <see cref="IllusionPyramidService"/>. Code 29008 is used only for a server fault.
/// </para>
/// </remarks>
public class IllusionPyramidDrawStageHandler : ZoneRmiHandlerBase<IllusionPyramidDrawStageHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqIllusionPyramidDrawStage;
    public override string Name => "IllusionPyramidDrawStage";

    public IllusionPyramidDrawStageHandler(IServiceScopeFactory s, ILogger<IllusionPyramidDrawStageHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (!IllusionPyramidService.TryParseDraw(payload.Span, out var req)) return;

        ushort result = await IllusionPyramidService.DrawAsync(player, req, services, Logger, ct);

        Logger.LogDebug("{Name} draws illusion pyramid stage {Stage} (mode {Mode:X2}, cost {CostKind:X2}:{CostRef}): {Result}",
            player.Name, req.Stage, req.Mode, req.CostFlags, req.CostRef, result);

        await session.SendRmiAsync(ZoneS2COpcodes.AnsIllusionPyramidDrawStage, IllusionPyramidService.Build2907(result), ct);
    }
}
