using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// C2S RMI 1667 ReqJobMasteryUpgrade <c>[u32 npcHandle]</c>: the master-rank-up button of a mastery NPC's dialog.
/// Answered once with 2989 AnsJobMasteryUpgrade.
/// </summary>
/// <remarks>
/// <para>
/// The reply is neutral. This server does not keep mastery level, grade or points, so the rank-up is never granted.
/// </para>
/// <para>
/// Missing system: a persistent per-character mastery level, grade and point total, with the rules of MasteryTable
/// and MasteryGradeTable. Once it exists, the rank-up check is data-driven: the level and point requirements, the
/// quest requirement from IQuestRepository, the coin cost, and on success result 0 plus the grade update.
/// </para>
/// </remarks>
public class JobMasteryUpgradeHandler : ZoneRmiHandlerBase<JobMasteryUpgradeHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqJobMasteryUpgrade;
    public override string Name => "JobMasteryUpgrade";

    public JobMasteryUpgradeHandler(IServiceScopeFactory s, ILogger<JobMasteryUpgradeHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (!JobMasteryPackets.TryReadNpcHandle(payload.Span, out var npcHandle)) return;

        Logger.LogDebug("{Name} asks NPC 0x{Npc:X8} for a Master rank-up; refused, no job-mastery state is kept",
            player.Name, npcHandle);

        await session.SendInternalRmiAsync(ZoneS2COpcodes.AnsJobMasteryUpgrade,
            JobMasteryPackets.BuildAnswer(JobMasteryPackets.UpgradeConditionNotMet, npcHandle), ct);
    }
}
