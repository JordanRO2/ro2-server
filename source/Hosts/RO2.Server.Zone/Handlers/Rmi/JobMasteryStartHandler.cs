using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// C2S RMI 1665 ReqJobMasteryStart <c>[u32 npcHandle]</c>: the start-master-level button of a mastery NPC's dialog.
/// Answered once with 2987 AnsJobMasteryStarter.
/// </summary>
/// <remarks>
/// <para>
/// The reply is neutral. This server does not keep job-mastery state, so the mastery is not started.
/// </para>
/// </remarks>
public class JobMasteryStartHandler : ZoneRmiHandlerBase<JobMasteryStartHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqJobMasteryStart;
    public override string Name => "JobMasteryStart";

    public JobMasteryStartHandler(IServiceScopeFactory s, ILogger<JobMasteryStartHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (!JobMasteryPackets.TryReadNpcHandle(payload.Span, out var npcHandle)) return;

        Logger.LogDebug("{Name} asks NPC 0x{Npc:X8} to start the Master level; refused, no job-mastery state is kept",
            player.Name, npcHandle);

        await session.SendInternalRmiAsync(ZoneS2COpcodes.AnsJobMasteryStarter,
            JobMasteryPackets.BuildAnswer(JobMasteryPackets.StartFailed, npcHandle), ct);
    }
}
