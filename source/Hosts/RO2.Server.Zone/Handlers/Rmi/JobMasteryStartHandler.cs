using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Characters.Domain;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// C2S RMI 1665 ReqJobMasteryStart <c>[u32 npcHandle]</c>: the start-master-level button of a mastery NPC's dialog.
/// A character at the base level cap that has not started yet converts to Master level 1 (rank 0, D); from then its
/// combat profession exp feeds the mastery point balance. Answered with 2987 AnsJobMasteryStarter.
/// </summary>
/// <remarks>
/// The master level is not carried by the answer; the client reads it from the PC view record, so the record is
/// re-sent to the map after the conversion for the level to show at once (see <see cref="PlayerSpawnService"/>).
/// </remarks>
public class JobMasteryStartHandler : ZoneRmiHandlerBase<JobMasteryStartHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqJobMasteryStart;
    public override string Name => "JobMasteryStart";

    public JobMasteryStartHandler(IServiceScopeFactory s, ILogger<JobMasteryStartHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (!JobMasteryPackets.TryReadNpcHandle(payload.Span, out var npcHandle)) return;

        var character = player.Character;
        if (character is null || !MasteryRules.CanStart(character.Level, character.MasteryLevel))
        {
            await session.SendInternalRmiAsync(ZoneS2COpcodes.AnsJobMasteryStarter,
                JobMasteryPackets.BuildAnswer(JobMasteryPackets.StartFailed, npcHandle), ct);
            return;
        }

        character.MasteryLevel = 1;
        character.MasteryRank = 0;
        await services.GetRequiredService<ICharacterRepository>()
            .UpdateJobMasteryAsync(player.CharacterId, character.MasteryLevel, character.MasteryRank, character.JobMasteryPoints);

        await session.SendInternalRmiAsync(ZoneS2COpcodes.AnsJobMasteryStarter,
            JobMasteryPackets.BuildAnswer(JobMasteryPackets.Accepted, npcHandle), ct);

        await MasteryProgression.ResendViewDataAsync(player, character, services, Broadcast);
        Logger.LogInformation("{Name} started the Master level at NPC 0x{Npc:X8}", player.Name, npcHandle);
    }
}
