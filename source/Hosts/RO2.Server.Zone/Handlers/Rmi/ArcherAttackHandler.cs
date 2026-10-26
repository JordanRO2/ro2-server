using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Worlds.Domain;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1360 ReqArcherAttack (ByteArray) - Ranged attack (archer class)</summary>
public class ArcherAttackHandler : ZoneRmiHandlerBase<ArcherAttackHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqArcherAttack;
    public override string Name => "ArcherAttack";

    public ArcherAttackHandler(IServiceScopeFactory s, ILogger<ArcherAttackHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        // ByteArray: [uint16 size][uint16 magic][uint32 targetEntityId][uint32 skillId]
        if (payload.Length < 12) return;
        var reader = new PacketReader(payload.Span);
        reader.Skip(4); // BA header
        var targetEntityId = reader.ReadUInt32();
        var skillId = reader.Remaining >= 4 ? reader.ReadUInt32() : 0u;

        Logger.LogDebug("{Name} archer attack on {Target} skill={SkillId}", player.Name, targetEntityId, skillId);

        // This request layout is not confirmed against live client traffic. The request is gated exactly like the
        // 1499 impact and answers nothing when refused: it needs a swing a 1498 armed (one impact per swing, bound
        // to that 1498's target and past the interval floor), and the attacker alive with the monster alive, on
        // its map and in reach. Without this a client could send 1360 at will, unmetered, from any map and any distance.
        var verdict = NormalAttackDamageHandler.TakeImpact(player, targetEntityId, DateTime.UtcNow);
        if (verdict != NormalAttackDamageHandler.SwingVerdict.Accepted)
        {
            Logger.LogDebug("{Name} archer attack on 0x{Target:X8} dropped ({Verdict})", player.Name, targetEntityId, verdict);
            return;
        }

        var npcState = services.GetRequiredService<NpcStateManager>();
        var npc = npcState.GetNpc(targetEntityId);
        if (npc is null || npc.Camp <= 1) return;                     // only monsters take damage
        var refusal = NormalAttackDamageHandler.GateBasicAttack(player, npc);
        if (refusal != ActorRefusal.None)
        {
            Logger.LogDebug("{Name} archer attack on 0x{Target:X8} refused ({Refusal})", player.Name, targetEntityId, refusal);
            return;
        }

        // Compute the hit AND APPLY IT. ApplyDamageAsync only computes and returns the number; the caller must
        // subtract it via NpcStateManager.ApplyDamage (the same two-step the live 1499/1509 handlers use).
        var combat = services.GetRequiredService<CombatService>();
        var (damage, kind) = await combat.ApplyDamageAsync(player, targetEntityId, skillId, 0);
        // The retail threat rule every hit path uses (ThreatAmount): damage x max(1, the skill's AddAggroRate) + its
        // AddAggro, times the stance rate; a normal hit (skill 0) has neither column.
        var threat = skillId != 0
            ? ThreatAmount.For(services.GetService<EffectService>(), player, damage,
                ThreatAmount.AddAggroOf(skillId), ThreatAmount.AddAggroRateOf(skillId))
            : ThreatAmount.For(services.GetService<EffectService>(), player, damage, ThreatAmount.NormalHitAddAggro);
        var (_, died) = npcState.ApplyDamage(targetEntityId, damage, player.EntityId, player.Name, threat);

        // The 2630 step right before the 2696 and the hit's 2643 target change after it, as the 1499 path sends them
        // (NpcCombatService.FlushAggroStepsAsync / PushPendingTargetChangesAsync); a kill sends no step.
        var npcCombat = services.GetService<NpcCombatService>();
        if (npcCombat is not null)
            await npcCombat.FlushAggroStepsAsync(ct);

        // Reflect the hit with 2696 NfyAttackDamage (the same builder the live melee path uses). The
        // ranged-specific 2525 OnArcherAttack body is not sent: its layout is not modelled and a raw body
        // crashes the client, so 2696 carries the damage number.
        await Broadcast.BroadcastToMapAsync(player.MapId, ZoneS2COpcodes.NfyAttackDamage,
            w => w.WriteBytes(NormalAttackDamageHandler.BuildNpcHit2696(player, npc, damage, kind)));   // kind: 1 normal / 4 crit (x2), decided in ApplyDamageAsync
        if (npcCombat is not null)
            await npcCombat.PushPendingTargetChangesAsync(ct);

        // EXP is granted ONLY when a monster dies (LootService, from NpcServerInfo.EXP_Reward), exactly
        // like melee; a ranged attack alone grants none.
        if (died)
        {
            AutoAttackState.Disengage(player);
            // The death panel, as at every other kill site: whoever has the monster selected gets its final
            // 2643 (HP 0, no target: the kill cleared its threat table), before the 2471 and the 2392, the
            // order NormalAttackDamageHandler uses for normal kills.
            await ReqActorDetailInfoHandler.PushIconToWatchersAsync(Broadcast, PlayerManager,
                npc.MapId, npc.EntityOid, ReqActorDetailInfoHandler.BuildIconOf(npc), ct);
            // Credit, then the 2471 grant, then the corpse: retail's kill order (LootService.ProcessKillCreditAsync).
            var loot = services.GetRequiredService<LootService>();
            await loot.ProcessKillCreditAsync(session, player, targetEntityId, npc.NpcId);
            await session.SendInternalRmiAsync(ZoneS2COpcodes.OnLootingAuthority,
                CombatPackets.BuildLootingAuthority2471(npc.EntityOid));
            await loot.ProcessNpcCorpseAsync(session, player, targetEntityId, npc.NpcId);
        }
    }
}
