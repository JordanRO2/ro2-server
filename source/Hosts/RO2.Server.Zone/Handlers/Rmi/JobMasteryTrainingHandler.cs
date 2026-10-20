using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// C2S RMI 1666 ReqJobMasteryTraining <c>[u32 npcHandle]</c>: the master-level training button of a mastery NPC's
/// dialog. Answered once with 2988 AnsJobMasteryTraining.
/// </summary>
/// <remarks>
/// <para>
/// The reply is neutral. This server keeps no mastery points, level or grade, so no training is applied.
/// </para>
/// </remarks>
public class JobMasteryTrainingHandler : ZoneRmiHandlerBase<JobMasteryTrainingHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqJobMasteryTraining;
    public override string Name => "JobMasteryTraining";

    public JobMasteryTrainingHandler(IServiceScopeFactory s, ILogger<JobMasteryTrainingHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (!JobMasteryPackets.TryReadNpcHandle(payload.Span, out var npcHandle)) return;

        Logger.LogDebug("{Name} asks NPC 0x{Npc:X8} for Master-level training; refused, no job-mastery points are kept",
            player.Name, npcHandle);

        await session.SendInternalRmiAsync(ZoneS2COpcodes.AnsJobMasteryTraining,
            JobMasteryPackets.BuildAnswer(JobMasteryPackets.TrainingConditionNotMet, npcHandle), ct);
    }
}
