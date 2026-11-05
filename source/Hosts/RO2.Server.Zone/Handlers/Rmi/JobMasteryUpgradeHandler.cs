using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Characters.Domain;
using RO2.GameData;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Quests.Domain;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// C2S RMI 1667 ReqJobMasteryUpgrade <c>[u32 npcHandle]</c>: the master-rank-up button of a mastery NPC's dialog.
/// At a rank's cap level, with the grade quest done and the points and coin paid, advances the Master rank by one
/// (D to C to B to A to S), which unlocks the next ten levels. Answered with 2989 AnsJobMasteryUpgrade.
/// </summary>
/// <remarks>
/// The requirements come from MasteryGradeTable (the character's combat-class row of the target grade): the level
/// reached, the quest, the point and coin cost. The new rank is not carried by the answer; the client reads it from
/// the re-sent PC view record. A grade-5 (S) row needs an unreachable quest, so the practical cap stays rank A.
/// </remarks>
public class JobMasteryUpgradeHandler : ZoneRmiHandlerBase<JobMasteryUpgradeHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqJobMasteryUpgrade;
    public override string Name => "JobMasteryUpgrade";

    public JobMasteryUpgradeHandler(IServiceScopeFactory s, ILogger<JobMasteryUpgradeHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (!JobMasteryPackets.TryReadNpcHandle(payload.Span, out var npcHandle)) return;

        var character = player.Character;
        var store = services.GetRequiredService<GameDataStore>();
        var req = character is null ? null
            : MasteryRules.RankUpRequirement(store, character.MasteryRank, CharacterHeadRules.CombatJobByte(character));

        bool ok = false;
        if (character is not null && req is { } r
            && character.MasteryLevel >= r.NeedLevel
            && character.JobMasteryPoints >= (ulong)r.NeedPoint * MasteryRules.PointScale
            && character.Gold >= r.NeedCoin)
        {
            ok = r.NeedQuestId == 0
                || await services.GetRequiredService<IQuestRepository>().GetLastCompletedAtAsync(player.CharacterId, r.NeedQuestId) is not null;
        }

        if (!ok || character is null || req is not { } rr)
        {
            await session.SendInternalRmiAsync(ZoneS2COpcodes.AnsJobMasteryUpgrade,
                JobMasteryPackets.BuildAnswer(JobMasteryPackets.UpgradeConditionNotMet, npcHandle), ct);
            return;
        }

        ulong costRaw = (ulong)rr.NeedPoint * MasteryRules.PointScale;
        character.JobMasteryPoints -= costRaw;
        character.Gold -= rr.NeedCoin;
        character.MasteryRank += 1;

        var repo = services.GetRequiredService<ICharacterRepository>();
        await repo.UpdateJobMasteryAsync(player.CharacterId, character.MasteryLevel, character.MasteryRank, character.JobMasteryPoints);
        await repo.UpdateGoldAsync(player.CharacterId, character.Gold);

        await MasteryProgression.ApplyAsync(session, player, character, services, Broadcast, pushStatSheet: false);
        if (costRaw > 0)
            await session.SendInternalRmiAsync(ZoneS2COpcodes.NfyJobMasteryPointChange,
                TraitPackets.BuildJobMasteryPointChange2986(-(long)costRaw, (uint)Math.Min(character.JobMasteryPoints, uint.MaxValue)), ct);
        await session.SendInternalRmiAsync(ZoneS2COpcodes.AnsJobMasteryUpgrade,
            JobMasteryPackets.BuildAnswer(JobMasteryPackets.Accepted, npcHandle), ct);

        Logger.LogInformation("{Name} raised Master rank to {Rank} (spent {Points} points, {Coin} coin)",
            player.Name, character.MasteryRank, rr.NeedPoint, rr.NeedCoin);
    }
}
