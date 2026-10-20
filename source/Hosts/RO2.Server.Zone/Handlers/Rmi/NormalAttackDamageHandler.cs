using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>RMI 1499 ReqDamageNormalAttack - Normal attack damage tick</summary>
public class NormalAttackDamageHandler : ZoneRmiHandlerBase<NormalAttackDamageHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqDamageNormalAttack;
    public override string Name => "NormalAttackDamage";

    /// <summary>
    /// 2695 code for an impact that belongs to no pending swing: a 1499 with no 1498 granted since the previous
    /// 1499 (the client can send an impact twice, a few milliseconds apart, and re-report after the 2696).
    /// Such impacts are refused with 20158 and deal no damage (see AutoAttackState.TryConsumeSwing). Kept here
    /// rather than in the shared CombatPackets, next to the only code that sends it.
    /// </summary>
    internal const ushort ImpactRefusedNoSwing = 20158;

    /// <summary>1499 body: 8 bytes, no envelope in front. The first u32 is not read; the target's actor handle is the u32 at <see cref="TargetHandleOffset"/>.</summary>
    private const int RequestBodyLength = 8;

    /// <summary>Offset of the target's actor handle (u32) inside the 1499 body.</summary>
    private const int TargetHandleOffset = 4;

    /// <summary>Mask of an actor handle's low byte, its actor type tag (0x11 PC, 0x13 NPC, 0x14 function object).</summary>
    private const uint ActorTagMask = 0xFF;

    /// <summary>Skill id passed to <see cref="CombatService.ApplyDamageAsync"/> and <see cref="CombatService.ResolvePvpHit"/> for a normal attack: no skill.</summary>
    private const uint NormalAttackSkillId = 0;

    /// <summary>Base damage passed to <see cref="CombatService.ApplyDamageAsync"/> for a normal attack: none, the damage comes from the attacker's stats.</summary>
    private const int NormalAttackBaseDamage = 0;

    /// <summary>Damage of a swing shown as a miss (damageKind <see cref="CombatPackets.DamageKindMiss"/>).</summary>
    private const int MissDamage = 0;

    public NormalAttackDamageHandler(IServiceScopeFactory s, ILogger<NormalAttackDamageHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < RequestBodyLength) return;
        var reader = new PacketReader(payload.Span);
        reader.Skip(TargetHandleOffset);
        var targetEntityId = reader.ReadUInt32();

        // ONE impact per granted swing. The first 1499 after a granted 1498 takes the swing; any later one is
        // refused with 20158 and deals nothing: no damage, no 2696, no weapon wear. A double-sent impact therefore
        // deals its damage once and the copy is refused. After every 20158 the client's next attack request is a new
        // 1498, so no disengage goes with it. The swing is consumed before anything else, so the unknown-target,
        // wrong-target and out-of-reach returns below spend it too and stay silent.
        // The swing is bound to the 1498's target and arming time (TakeImpact): an impact on another target, or one
        // that lands sooner than the interval floor after its 1498, deals nothing.
        var verdict = TakeImpact(player, targetEntityId, DateTime.UtcNow);
        if (verdict == SwingVerdict.NoSwingPending)
        {
            Logger.LogDebug("{Name}: 1499 on 0x{Target:X8} with no swing pending; refused with 20158",
                player.Name, targetEntityId);
            await session.SendInternalRmiAsync(ZoneS2COpcodes.AnsErrorNormalAttack,
                CombatPackets.BuildAttackRefused2695(ImpactRefusedNoSwing), ct);
            return;
        }
        if (verdict != SwingVerdict.Accepted)
        {
            // No reply is defined for an impact on another target than its 1498's, or one inside the interval floor;
            // the server stays silent.
            Logger.LogDebug("{Name}: 1499 on 0x{Target:X8} dropped ({Verdict})", player.Name, targetEntityId, verdict);
            return;
        }

        // Every landed OR missed swing is a combat action: keep the player in battle mode (2398 0x0C). MarkCombat
        // on the 1498 start alone is not enough -- a sustained auto-attack streams 1499 impacts without re-sending a
        // 1498 each swing, so the stance would time out (~5 s) mid-fight. This refreshes it on every swing instead.
        services.GetService<PcCombatStateService>()?.MarkCombat(player);

        var npcState = services.GetRequiredService<NpcStateManager>();
        var combat = services.GetRequiredService<CombatService>();

        var npcRuntime = npcState.GetNpc(targetEntityId);
        if (npcRuntime is null && (targetEntityId & ActorTagMask) == PlayerStateManager.PcActorTag)
        {
            await LandPvpHitAsync(session, player, targetEntityId, combat, services, ct);
            return;
        }
        if (npcRuntime is null)
        {
            // Worth a warning rather than silence: if this fires in a live session, the request's parse needs
            // checking (other C2S messages in this family carry the compact envelope with the actor handle at blob+4,
            // so a blind Skip(4) would read the msgTag).
            Logger.LogWarning("Session {Id}: attack target {Target:X8} is not a known NPC",
                session.SessionId, targetEntityId);
            return;
        }

        // Only monsters (Camp >= 2) take damage, not friendly NPCs.
        if (npcRuntime.Camp <= NpcSpawnService.CampFriendly) return;

        // A monster running home after a reset is untouchable (NpcRuntimeState.IsReturning). Show the swing as a
        // MISS (damageKind 2, 0 damage) -- no threat, no durability wear, no 2503 -- and do it BEFORE the reach
        // gate: during the chase home the monster is out of reach, which would otherwise silence the swing and the
        // player would never see why no damage lands. ApplyDamageAsync also returns the miss (it guards the other
        // damage paths), but here we skip straight to the frame so the reach gate cannot eat it.
        if (npcRuntime.IsReturning(DateTime.UtcNow))
        {
            var missBody = BuildNpcHit2696(player, npcRuntime, MissDamage, CombatPackets.DamageKindMiss);
            await Broadcast.BroadcastToMapAsync(player.MapId, ZoneS2COpcodes.NfyAttackDamage,
                writer => writer.WriteBytes(missBody));
            return;
        }

        // The client reports its own impacts, so the server validates them here rather than
        // trusting them: the attacker alive, the monster alive and on the attacker's map, and in reach. Range is
        // checked at the moment damage lands, which is also the point the player's position is most current.
        var refusal = GateBasicAttack(player, npcRuntime);
        if (refusal != ActorRefusal.None)
        {
            Logger.LogDebug("{Name} reported a hit on 0x{Target:X8} refused ({Refusal})", player.Name,
                targetEntityId, refusal);
            return;
        }

        var (damage, kind) = await combat.ApplyDamageAsync(player, targetEntityId, NormalAttackSkillId, NormalAttackBaseDamage);

        // The attacker is passed so the hit banks threat on the monster's table. A normal hit has no AddAggro, so the
        // amount is the damage times the attacker's stance rate (ThreatAmount: the trait-49 StanceAggroRate effects).
        // The stance is not modelled: no path here applies a trait-49 effect to a player (skill self-buffs and pet
        // effects never reach ActiveEffects), so the rate is 0 and every hit banks the damage x1. This per-hit
        // banking replaces NpcCombatService's old per-tick "Str*2 + Level*3" accrual.
        var threat = ThreatAmount.For(services.GetService<EffectService>(), player, damage, ThreatAmount.NormalHitAddAggro);
        var (_, died) = npcState.ApplyDamage(targetEntityId, damage, player.EntityId, player.Name, threat);

        // The hit's 2630 threat step goes out now, right before its 2696, not on NpcCombatService's next 400 ms pass
        // (see NpcCombatService.FlushAggroChangesAsync). A killing hit gets none: the flush drops a dead monster's
        // steps. The 2643 target change the hit causes goes out after the 2696 (PushPendingTargetChangesAsync
        // below), as the 1549 path does, and never between the step and the frame.
        // A container without the service only leaves both to the pass.
        var npcCombat = services.GetService<NpcCombatService>();
        if (npcCombat is not null)
            await npcCombat.FlushAggroStepsAsync(ct);

        // 2696 carries the victim's HP AFTER this hit, so the broadcast has to follow the damage
        // application -- it used to precede it. It also used to be a 13-byte RAW body keyed by
        // SessionId, which is a guaranteed client crash for this family. Note the two actor records
        // are 2-mod-4 misaligned; see CombatPackets.BuildAttackDamage2696.
        var body = BuildNpcHit2696(player, npcRuntime, damage, kind);   // kind: 1 normal / 4 crit (x2), decided in ApplyDamageAsync
        await Broadcast.BroadcastToMapAsync(player.MapId, ZoneS2COpcodes.NfyAttackDamage,
            writer => writer.WriteBytes(body));
        if (npcCombat is not null)
            await npcCombat.PushPendingTargetChangesAsync(ct);

        // Weapon durability wear: every worn weapon-slot item (Equip_Slot 6, then 7) loses one point,
        // and only the attacker is told, one RMI 2503 OnUpdateItemDurability per item. The message
        // carries no actor handle and edits the local player's own inventory, so it is unicast to the
        // attacker's session, never a broadcast. Wear applies to every swing that did not miss, whatever the
        // damage number (see DurabilityService.ApplyWeaponWearOnHitAsync for the slot rule).
        // The armour case (hits TAKEN) belongs to the monster's retaliation pass (NpcCombatService).
        if (kind != CombatPackets.DamageKindMiss)
        {
            var durability = services.GetRequiredService<DurabilityService>();
            await durability.ApplyWeaponWearOnHitAsync(session, player, services, ct);
        }

        // While the Illusion Pyramid is armed, a 2696 that names the PC is followed by a 29005 close after the
        // hit's 2503s (IllusionPyramidService, COMBAT). A miss counts too: a 2696 still went out.
        await IllusionPyramidService.NotifyCombatAsync(session, player, Logger, ct);

        // Do not send a PC OnChangeState(PcNormal=0) here. The attacking client enters its combat/weapon-drawn
        // stance by prediction the moment it swings, and broadcasting PcNormal to the map includes the attacker
        // (BroadcastToMapAsync does not exclude the source), so it would cancel the predicted stance and pin
        // locomotion to normal: the avatar would never draw its weapon and would keep the run animation. The
        // stance is client-side and is not overwritten per swing.
        //
        // The NPC combat state is different: the monster's own 2398 (state 3, naming this attacker) does not
        // touch the player's actor. NpcChatService sends it, together with the combat line (2729 NfyNpcChat,
        // Npc_Dlg situation 02) right after it. Both go out only on the hit that starts the engagement, not on
        // every hit (see NpcChatService.EnterCombatAsync).
        var npcChat = services.GetRequiredService<NpcChatService>();
        await npcChat.EnterCombatAsync(npcRuntime, player.EntityId);

        // No per-hit 2643 AnsActorIconInfo is sent to the attacker. The panel's HP comes from the 2696 above
        // (victim HP after the hit). The unsolicited 2643 goes to the players who have the actor selected, when
        // its target changes (drained with the 2630s in NpcCombatService) or when it dies (below).

        if (died)
        {
            AutoAttackState.Disengage(player);
            // No 2398 is sent for the corpse; the skill-kill paths (SkillDamageHandler, SkillDamageTickHandler)
            // send none either. Close the engagement, so the respawned monster's next state 3 carries its combat
            // line again.
            npcChat.OnNpcLeftCombat(npcRuntime.EntityOid);

            // Everyone who has the monster selected gets its final panel: HP 0 and no target (the kill cleared
            // its threat table). It goes out before the 2392 that ProcessNpcCorpseAsync below sends.
            await ReqActorDetailInfoHandler.PushIconToWatchersAsync(Broadcast, PlayerManager,
                npcRuntime.MapId, npcRuntime.EntityOid, ReqActorDetailInfoHandler.BuildIconOf(npcRuntime), ct);

            // 2471 OnLootingAuthority is sent before the death notify. 2392 is emitted inside
            // ProcessNpcCorpseAsync below, so the grant has to go out first.
            //
            // Unicast, not a broadcast: the receiving client adds the handle to its own loot list, so
            // broadcasting it would hand loot rights for this kill to every client on the map. Without it the
            // corpse never enters that list, the loot indicator never lights it, and the loot window has nothing
            // to open on. It goes to the killer over the internal (unencrypted) path.
            //
            // The kill's credit goes out before that grant, and the corpse after it, as for a normal kill: the
            // 2425 quest tick, 2427 and 2577 precede the 2471, and the 2393 comes right before it (the skill-kill
            // path, SkillDamageHandler, has the same split).
            var loot = services.GetRequiredService<LootService>();
            await loot.ProcessKillCreditAsync(session, player, targetEntityId, npcRuntime.NpcId);
            await session.SendInternalRmiAsync(ZoneS2COpcodes.OnLootingAuthority,
                CombatPackets.BuildLootingAuthority2471(npcRuntime.EntityOid));
            await loot.ProcessNpcCorpseAsync(session, player, targetEntityId, npcRuntime.NpcId);
        }
    }

    /// <summary>What <see cref="TakeImpact"/> decided about a 1499 (or 1360) impact.</summary>
    internal enum SwingVerdict
    {
        /// <summary>The impact takes the swing the latest 1498 armed.</summary>
        Accepted,
        /// <summary>No swing pending (never granted, or already taken): answered with 20158.</summary>
        NoSwingPending,
        /// <summary>A swing is pending but the impact arrived inside the interval floor; it stays pending.</summary>
        TooEarly,
        /// <summary>The swing was taken (spent) but the impact names another target than its 1498 did.</summary>
        WrongTarget,
    }

    /// <summary>
    /// Takes the pending swing for an impact on <paramref name="targetEntityId"/> at <paramref name="now"/>
    /// (<see cref="AutoAttackState.TryConsumeSwing(PlayerState, DateTime, TimeSpan, out uint)"/> with the
    /// <see cref="AutoAttackState.DefaultMinSwingInterval"/> floor) and requires the impact's target to be the one
    /// the 1498 named. A too-early impact does not spend the swing; a wrong-target one does.
    /// </summary>
    internal static SwingVerdict TakeImpact(PlayerState player, uint targetEntityId, DateTime now)
    {
        if (!AutoAttackState.TryConsumeSwing(player, now, AutoAttackState.DefaultMinSwingInterval, out var bound))
            return bound == 0 ? SwingVerdict.NoSwingPending : SwingVerdict.TooEarly;
        return bound == targetEntityId ? SwingVerdict.Accepted : SwingVerdict.WrongTarget;
    }

    /// <summary>
    /// The shared precondition of a basic attack on a monster: attacker alive, monster alive, same map and within
    /// the attacker's weapon reach (<see cref="ActorGate.MeleeReach"/>: Auto_Attack_Range, else the default melee
    /// reach, plus the client's own in-range tolerance).
    /// </summary>
    internal static ActorRefusal GateBasicAttack(PlayerState player, NpcRuntimeState npc)
        => ActorGate.CanAct(ActorGate.ActorOf(player), ActorGate.TargetOf(npc), new GateContext(ActorGate.MeleeReach(player)));

    /// <summary>
    /// The 2696 for a hit on a monster. The victim's SP is the NPC's real one (<see cref="SkillDamagePackets.NpcTargetSp"/>,
    /// the figure its 2506 record, 2643 and the skill paths advertise): the client stores it into the victim's SP
    /// gauge, so it is never sent as 0 for an NPC victim.
    /// </summary>
    internal static byte[] BuildNpcHit2696(PlayerState player, NpcRuntimeState npc, int damage, byte kind)
        => CombatPackets.BuildAttackDamage2696(
            attackerHandle: player.EntityId,
            attackerCurHp: (uint)Math.Max(0, player.Hp),
            attackerCurSp: (ushort)Math.Clamp(player.Mp, 0, ushort.MaxValue),
            victimHandle: npc.EntityOid,
            victimCurHpAfter: (uint)Math.Max(0, npc.Hp),
            victimCurSp: (ushort)Math.Min(SkillDamagePackets.NpcTargetSp(npc), ushort.MaxValue),
            damage: (uint)Math.Max(0, damage),
            damageKind: kind);

    /// <summary>
    /// A normal hit on another player: only on this player's opponent in a started field duel
    /// (FieldPvPCombat.TryGetStartedOpponent), in reach of its last reported position, answered like a hit on a
    /// monster with the map-wide 2696 carrying the victim's HP after the hit and its SP.
    /// </summary>
    /// <remarks>
    /// A hit that would take the victim to 0 leaves it at HP 1 and ends the duel as fought out before the 2696
    /// goes out (the 2736 and its companions, then the lethal 2696 with HP 1). The attacker's weapons wear as
    /// after a hit on a monster (the wear rule is the swing's, not the target's), and the victim's armour as
    /// after a monster's hit on it (DurabilityService.ApplyArmourWearOnTakenHitAsync). No monster state, threat
    /// or loot is involved.
    /// </remarks>
    private async Task LandPvpHitAsync(ProudSession session, PlayerState player, uint targetHandle,
        CombatService combat, IServiceProvider services, CancellationToken ct)
    {
        if (FieldPvPCombat.TryGetStartedOpponent(player, targetHandle, PlayerManager) is not { } opponent)
        {
            Logger.LogDebug("{Name} reported a hit on PC 0x{Target:X8}, which is not a started-duel opponent",
                player.Name, targetHandle);
            return;
        }
        var victim = opponent.Victim;
        if (!AutoAttackState.InRange(player, victim.X, victim.Y))
        {
            Logger.LogDebug("{Name} reported a hit on 0x{Target:X8} out of reach", player.Name, targetHandle);
            return;
        }

        var (damage, kind) = combat.ResolvePvpHit(player, victim, NormalAttackSkillId);
        if (FieldPvPCombat.TakeHit(victim, damage))
            await FieldPvPCombat.EndFoughtOutAsync(opponent.Duel, player, victim, services, Broadcast, ct);

        var body = CombatPackets.BuildAttackDamage2696(
            attackerHandle: player.EntityId,
            attackerCurHp: (uint)Math.Max(0, player.Hp),
            attackerCurSp: (ushort)Math.Clamp(player.Mp, 0, ushort.MaxValue),
            victimHandle: victim.EntityId,
            victimCurHpAfter: (uint)Math.Max(0, victim.Hp),
            victimCurSp: (ushort)Math.Clamp(victim.Mp, 0, ushort.MaxValue),
            damage: (uint)Math.Max(0, damage),
            damageKind: kind);
        await Broadcast.BroadcastToMapAsync(player.MapId, ZoneS2COpcodes.NfyAttackDamage,
            writer => writer.WriteBytes(body));

        if (kind != CombatPackets.DamageKindMiss)
        {
            var durability = services.GetRequiredService<DurabilityService>();
            await durability.ApplyWeaponWearOnHitAsync(session, player, services, ct);
            await durability.ApplyArmourWearOnTakenHitAsync(victim, ct);
        }

        // The pyramid's combat notice to both PCs the 2696 names, as after a hit on or by a monster
        // (the rule is "a 2696 naming the PC", IllusionPyramidService, COMBAT).
        await IllusionPyramidService.NotifyCombatAsync(session, player, Logger, ct);
        if (Broadcast.GetSession(victim.SessionId) is { IsConnected: true } victimSession)
            await IllusionPyramidService.NotifyCombatAsync(victimSession, victim, Logger, ct);
    }
}
