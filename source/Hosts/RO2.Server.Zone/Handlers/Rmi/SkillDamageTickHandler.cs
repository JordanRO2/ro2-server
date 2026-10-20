using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// C2S RMI 1549 ReqSkillDamageTick — one periodic tick of a channelled or over-time skill. The
/// answer is the broadcast RMI 2797 NfySkillDamageTick.
/// </summary>
/// <remarks>
/// <para>
/// <b>THE REQUEST'S FIRST FIELD IS THE SKILL ID, NOT A TARGET.</b> body+0 resolves in
/// <c>data/Skill_Table_SkillLevel.json</c>, and 2797's blob+8, which is the skill id, echoes it.
/// The request is a fixed 5 bytes, <c>[u32 skillId][u8 tickSequence]</c>, and the tick byte is echoed
/// too in 2797's blob+12: it is a sequence, not a flag.
/// </para>
/// <para>
/// <b>The reply body needs the compact envelope.</b> A bare body starting with <c>player.SessionId</c>
/// would have a first byte (the session id's low byte) outside the compact-length marker set {1,2,4,8}
/// for most session ids. A body the decoder rejects makes the per-opcode stub hand the consumer a NULL
/// blob, a remote-crash path.
/// </para>
/// <para>
/// RECIPIENT: <b>map broadcast, caster INCLUDED.</b> A 2797 with the same (skillId, tick) reaches the
/// requesting client itself, so <c>BroadcastToNearbyAsync</c>, which excludes the source session, is the
/// wrong helper.
/// </para>
/// <para>
/// <b>THE HIT SET is the set the same skill's last 2718 carried.</b> The request names no actor, and a
/// tick lists every actor it hit: the 2797 of a tick hits exactly the
/// actors of the 2718 that answered the client's latest 1509 of the same skill id, or a strict subset of
/// them (the rest had died), in that 2718's order. SkillDamageHandler
/// records what its 2718 carried in <see cref="SkillTickTargets"/>, and this handler ticks those actors that
/// are still alive, on this map and hostile (<see cref="SkillTickTargets.LiveTargets"/>). Ticking only the
/// caster's auto-attack target would drop every other actor of an area cast.
/// </para>
/// <para>
/// No 2797 goes out when no recorded actor is left: the count is at least 1, so an empty vector is a shape
/// that is never sent, and the builder refuses it. A tick with no record (no 1509 of that skill answered on
/// this map) is not answered either.
/// </para>
/// <para>
/// A tick's PC targets are hit only when the PC is the caster's opponent in a started field duel
/// (<see cref="TickPvpAsync"/>); other PvP ticks have
/// no model here, and neither does a heal skill's tick (Skill_Type 3, see the gate below) nor any NPC-cast tick
/// (no NPC casts skills on this server).
/// </para>
/// </remarks>
public class SkillDamageTickHandler : ZoneRmiHandlerBase<SkillDamageTickHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqSkillDamageTick;
    public override string Name => "SkillDamageTick";

    /// <summary>C2S 1549 is a fixed 5 bytes: [u32 skillId][u8 tickSequence].</summary>
    private const int RequestBodyLen = 5;

    /// <summary>Base damage passed to <see cref="CombatService.ApplyDamageAsync"/> for a tick: none, the damage is derived from the caster's stats and the skill.</summary>
    private const int DamageFromCasterStats = 0;

    /// <summary>Set once the missing-table warning of <see cref="SkillDamagePackets.SkillTypeTableError"/> is logged.</summary>
    private static bool _skillTypeTableErrorLogged;

    public SkillDamageTickHandler(IServiceScopeFactory s, ILogger<SkillDamageTickHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    /// <summary>Time source for the pending-cast grace and the tick ledger's interval floor. The real clock in
    /// production; a test sets it so the interval floor does not hinge on how much wall-clock time a loopback round
    /// trip took (as <see cref="SkillStartHandler.Clock"/>).</summary>
    internal Func<DateTime> Clock { get; init; } = static () => DateTime.UtcNow;

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        if (payload.Length < RequestBodyLen) return;

        uint skillId;
        byte tickSequence;
        {
            var reader = new PacketReader(payload.Span);
            skillId = reader.ReadUInt32();      // NOT a target handle; see the remarks
            tickSequence = reader.ReadByte();   // echoed straight back; do not invent a counter
        }

        // Only an attack skill's tick (Skill_Table_Base.Skill_Type 1) damages anything. A tick of a
        // buff/aura/self skill (types 2, 4, 10) is not answered, and a heal skill's tick (type 3) heals PCs, which
        // this server does not model. Damaging the current combat target on every tick of every skill (e.g. each
        // 631910 Cure tick) would be wrong. See SkillDamagePackets.IsDamageTickSkill.
        if (!SkillDamagePackets.IsDamageTickSkill(skillId))
        {
            if (SkillDamagePackets.SkillTypeTableError is { } tableError && !_skillTypeTableErrorLogged)
            {
                _skillTypeTableErrorLogged = true;
                Logger.LogWarning("Skill damage ticks are disabled: {Error}", tableError);
            }
            return;
        }

        // A tick damages only within a cast this caster legitimately started: an accepted
        // 1508 of this skill left a pending cast still inside its grace (SkillCastLandings). Ticks repeat within one
        // cast, so the entry is required but NOT consumed. A forged 1549 with no such cast deals no damage and is not
        // answered (no 2797), like an unbacked tick with no prior 1509/2718 of the skill.
        var now = Clock();
        if (!SkillCastLandings.TryGovern(player, skillId, now, consume: false, out _))
        {
            Logger.LogDebug("{Name}: 1549 tick of skill {SkillId} refused, no pending cast within grace",
                player.Name, skillId);
            return;
        }

        // The cast's tick ledger. Without it nothing would bound how many ticks arrive, how fast, or in what order,
        // so one cast would buy unlimited hits. A tick is refused (no damage, no
        // 2797) unless its sequence is strictly above the last accepted one, it is at least
        // SkillCastLandings.MinTickInterval after it, and the cast has had fewer than MaxTicksPerCast; the ledger
        // restarts with a fresh accepted 1509 / a new accepted cast / the cast's end (SkillCastLandings.TryAcceptTick).
        // ActiveChannelSkillId is deliberately NOT required to equal the skill: channelled skills emit no 1549
        // ticks, so that check would refuse the real ones.
        if (!SkillCastLandings.TryAcceptTick(player, skillId, tickSequence, now))
        {
            Logger.LogDebug("{Name}: 1549 tick {Tick} of skill {SkillId} refused by the tick ledger (order, rate or count)",
                player.Name, tickSequence, skillId);
            return;
        }

        // The actors the skill's last 2718 carried (SkillDamageHandler records them), less the ones that
        // have died, left the map or are not monsters. Nothing recorded, or nobody left: no 2797.
        var npcState = services.GetRequiredService<NpcStateManager>();
        var recorded = SkillTickTargets.Recorded(player, player.MapId, skillId);
        var targets = SkillTickTargets.LiveTargets(recorded, player.MapId, npcState.GetNpc);
        // The caster's started-duel opponent, when the skill's last 2718 hit it (SkillDamageHandler records PC
        // hits too): PC ticks hit PCs (elements with the 0x11 tag).
        var opponents = FieldPvPCombat.OpponentsIn(recorded, player, PlayerManager);
        if (targets.Count == 0 && opponents.Count == 0)
        {
            Logger.LogDebug("{Name}: tick {Tick} of skill {SkillId} has no live actor left of its 2718 ({Recorded} recorded); no 2797",
                player.Name, tickSequence, skillId, recorded.Count);
            return;
        }

        var combat = services.GetRequiredService<CombatService>();
        var effects = services.GetService<EffectService>();
        var hits = new List<SkillTickHit>(targets.Count + opponents.Count);
        // The handle is kept as it was hit: the corpse keeps it even once the NPC respawns under a fresh one.
        var killed = new List<(NpcRuntimeState Npc, uint Handle)>();
        // In the 2718's order (SkillTickTargets), monsters and the duel opponent alike, at most MaxTargets.
        var npcByHandle = targets.ToDictionary(t => t.Handle, t => t.Npc);
        foreach (var targetHandle in recorded)
        {
            if (hits.Count == SkillTickTargets.MaxTargets)
                break;
            if (opponents.TryGetValue(targetHandle, out var opponent))
            {
                hits.Add(await TickPvpAsync(player, opponent, skillId, combat, services, ct));
                continue;
            }
            if (!npcByHandle.TryGetValue(targetHandle, out var npcRuntime))
                continue;

            var (damage, kind) = await combat.ApplyDamageAsync(player, targetHandle, skillId, DamageFromCasterStats);

            // The attacker is passed so the tick keeps the caster on the monster's threat table (2629/2630 and
            // the 2643 target-of-target read it), as the 1499 and 1509 paths do. The amount is
            // (damage + AddAggro of the tick's skill level) x the caster's stance rate (ThreatAmount): each
            // 2630 precedes the 2797 in the same millisecond, and the step is damage + 1 (or (damage + 1) x 4 / x4.5 with
            // a stance rate). The x4 / x4.5
            // is not modelled (ThreatAmount remarks): no path applies a trait-49 effect to a player, so the rate is 0
            // and every tick banks (d + 1) x1. A level's AddAggroRate of 2 or
            // more multiplies the damage first: the step is (d x rate + 1) x m.
            var threat = ThreatAmount.For(effects, player, damage, ThreatAmount.AddAggroOf(skillId),
                ThreatAmount.AddAggroRateOf(skillId));
            var (_, died) = npcState.ApplyDamage(targetHandle, damage, player.EntityId, player.Name, threat);

            // The element carries POST-tick vitals, so it is built after the damage application.
            // elem+8 is written into the TARGET's SP by the client, and a non-zero value is expected
            // for an NPC target (one value per handle, equal to that handle's 2506/2317 record SP).
            // Every target here is an NPC, so it gets the SP
            // this server advertised for it in 2506; see SkillDamagePackets.NpcTargetSp.
            hits.Add(new SkillTickHit(
                targetHandle,
                (uint)Math.Max(0, npcRuntime.Hp),
                SkillDamagePackets.NpcTargetSp(npcRuntime),
                new SkillHitEffect(kind, damage)));   // kind 1 normal / 4 crit (x2)

            if (died)
                killed.Add((npcRuntime, targetHandle));
        }

        // The tick's 2630 threat steps right before the 2797, in its millisecond
        // (NpcCombatService.FlushAggroChangesAsync). A monster this tick killed gets none. Only the steps: the
        // 2643 target changes they cause go out AFTER the 2797 and never between the step and the frame
        // (see FlushAggroChangesAsync). A container without the service leaves both to the 400 ms pass.
        var npcCombat = services.GetService<NpcCombatService>();
        if (npcCombat is not null)
            await npcCombat.FlushAggroStepsAsync(ct);

        var notify = SkillDamagePackets.BuildSkillDamageTick2797(
            casterHandle: player.EntityId,
            skillId: skillId,
            tickSequence: tickSequence,
            hits: hits);

        await Broadcast.BroadcastToMapAsync(player.MapId, ZoneS2COpcodes.NfySkillDamageTick,
            writer => writer.WriteBytes(notify));

        if (npcCombat is not null)
            await npcCombat.PushPendingTargetChangesAsync(ct);

        if (killed.Count == 0)
            return;

        AutoAttackState.Disengage(player);
        var loot = services.GetRequiredService<LootService>();
        var npcChat = services.GetRequiredService<NpcChatService>();
        foreach (var (npcRuntime, targetHandle) in killed)
        {
            // The death panel, as at every other kill site: whoever has the monster selected gets its final
            // 2643 (HP 0, no target: the kill cleared its threat table), before the 2471 grant and the 2392.
            // It goes before the 2392 for normal kills (see NormalAttackDamageHandler) and before the 2471 and
            // the 2718 for 1509 skill kills (see SkillDamageHandler).
            await ReqActorDetailInfoHandler.PushIconToWatchersAsync(Broadcast, PlayerManager,
                npcRuntime.MapId, targetHandle, ReqActorDetailInfoHandler.BuildIconOf(npcRuntime), ct);

            // Credit, then the 2471 grant, then the corpse (LootService.ProcessKillCreditAsync),
            // run for each monster this tick killed, as SkillDamageHandler does for a 1509 that kills several.
            await loot.ProcessKillCreditAsync(session, player, targetHandle, npcRuntime.NpcId);

            await session.SendInternalRmiAsync(ZoneS2COpcodes.OnLootingAuthority,
                CombatPackets.BuildLootingAuthority2471(targetHandle));

            // Close the engagement now rather than at the next NpcChatService sweep, so the respawned
            // monster's first hit carries its state 3 and combat line again.
            npcChat.OnNpcLeftCombat(targetHandle);

            await loot.ProcessNpcCorpseAsync(session, player, targetHandle, npcRuntime.NpcId);
        }
    }

    /// <summary>
    /// One tick on the caster's started-duel opponent (FieldPvPCombat): damage resolved against the victim
    /// (CombatService.ResolvePvpHit), taken off its HP, and the 2797 element with its HP after the tick and its
    /// SP. A tick that would take it to 0 leaves it at HP 1 and ends the duel as fought out right here, BEFORE the
    /// 2797 this handler broadcasts after its loop: the duel end comes after the 2736 and its companions and the
    /// lethal 2797 carries HP 1.
    /// </summary>
    private async Task<SkillTickHit> TickPvpAsync(PlayerState caster, FieldPvPCombat.Opponent opponent,
        uint skillId, CombatService combat, IServiceProvider services, CancellationToken ct)
    {
        var victim = opponent.Victim;
        var (damage, kind) = combat.ResolvePvpHit(caster, victim, skillId);
        if (FieldPvPCombat.TakeHit(victim, damage))
        {
            await FieldPvPCombat.EndFoughtOutAsync(opponent.Duel, caster, victim, services, Broadcast, ct);
            Logger.LogInformation("{Name} won duel {Id} against {Other} with a tick of skill {SkillId}",
                caster.Name, opponent.Duel.Id, victim.Name, skillId);
        }
        return new SkillTickHit(
            victim.EntityId,
            (uint)Math.Max(0, victim.Hp),
            (uint)Math.Clamp(victim.Mp, 0, ushort.MaxValue),
            new SkillHitEffect(kind, damage));
    }
}
