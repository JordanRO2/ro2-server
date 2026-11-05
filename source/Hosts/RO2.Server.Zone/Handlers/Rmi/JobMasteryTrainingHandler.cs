using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Characters.Domain;
using RO2.GameData;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// C2S RMI 1666 ReqJobMasteryTraining <c>[u32 npcHandle]</c>: the master-level-up button of a mastery NPC's dialog.
/// Spends the next level's mastery points and coin to raise the Master level by one, within the current rank's cap.
/// Answered with 2988 AnsJobMasteryTraining.
/// </summary>
/// <remarks>
/// The new level is not carried by the answer; the client reads it from the PC view record, so the record is re-sent
/// to the map after the spend. The point balance change rides a 2986 notify. A grade-up (unlocking the next rank's
/// cap) is the separate 1667 request.
/// </remarks>
public class JobMasteryTrainingHandler : ZoneRmiHandlerBase<JobMasteryTrainingHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqJobMasteryTraining;
    public override string Name => "JobMasteryTraining";

    public JobMasteryTrainingHandler(IServiceScopeFactory s, ILogger<JobMasteryTrainingHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (!JobMasteryPackets.TryReadNpcHandle(payload.Span, out var npcHandle)) return;

        var character = player.Character;
        var store = services.GetRequiredService<GameDataStore>();
        const ulong scale = MasteryRules.PointScale;   // wire fact: raw balance = displayed points * 1000
        var cost = character is null ? null : MasteryRules.LevelUpCost(store, character.MasteryLevel, character.MasteryRank);

        // The table cost is in displayed mastery points; the stored balance is raw (x PointScale), see MasteryRules.
        if (character is null || cost is not { } c
            || character.JobMasteryPoints < (ulong)c.Points * scale || character.Gold < c.Coin)
        {
            await session.SendInternalRmiAsync(ZoneS2COpcodes.AnsJobMasteryTraining,
                JobMasteryPackets.BuildAnswer(JobMasteryPackets.TrainingConditionNotMet, npcHandle), ct);
            return;
        }

        // Gold goes through the atomic wallet before any point is touched, so a refusal leaves nothing to revert.
        if (!player.TryDebit(c.Coin))
        {
            await session.SendInternalRmiAsync(ZoneS2COpcodes.AnsJobMasteryTraining,
                JobMasteryPackets.BuildAnswer(JobMasteryPackets.TrainingConditionNotMet, npcHandle), ct);
            return;
        }

        ulong costRaw = (ulong)c.Points * scale;
        character.JobMasteryPoints -= costRaw;
        character.MasteryLevel += 1;

        var repo = services.GetRequiredService<ICharacterRepository>();
        await repo.UpdateJobMasteryAsync(player.CharacterId, character.MasteryLevel, character.MasteryRank, character.JobMasteryPoints);
        await repo.UpdateGoldAsync(player.CharacterId, player.Gold);

        await MasteryProgression.ApplyAsync(session, player, character, services, Broadcast, pushStatSheet: true);
        await session.SendInternalRmiAsync(ZoneS2COpcodes.NfyJobMasteryPointChange,
            TraitPackets.BuildJobMasteryPointChange2986(-(long)costRaw, (uint)Math.Min(character.JobMasteryPoints, uint.MaxValue)), ct);
        await session.SendInternalRmiAsync(ZoneS2COpcodes.AnsJobMasteryTraining,
            JobMasteryPackets.BuildAnswer(JobMasteryPackets.Accepted, npcHandle), ct);

        Logger.LogInformation("{Name} raised Master level to {Level} (spent {Points} points, {Coin} coin)",
            player.Name, character.MasteryLevel, c.Points, c.Coin);
    }
}
