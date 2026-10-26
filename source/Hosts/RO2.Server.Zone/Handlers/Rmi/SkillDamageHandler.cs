using Microsoft.Extensions.DependencyInjection;
using RO2.Skills.Domain;
using Microsoft.Extensions.Logging;
using RO2.GameData;
using RO2.Persistence.Mongo.Persistence;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;

namespace RO2.Server.Zone.Handlers.Rmi;

/// <summary>
/// C2S RMI 1509 ReqSkillDamage — the client reports that a skill it cast has landed, and the server
/// answers with the broadcast RMI 2718 NfySkillDamage.
/// </summary>
/// <remarks>
/// <para>
/// <b>Request layout.</b> The request is
/// <c>[u32 blobLen][blob]</c> with
/// </para>
/// <code>
///   blob+0  u32 skillId
///   blob+4  u8  1 iff a primary target follows
///   blob+5  u32 primaryTargetHandle
///   blob+9  f32 x, y, z          the position 2718 carries: zero when there is a primary target
///   blob+21 f32 x, y, z          the cast direction (not echoed)
///   blob+33 u8  castType
///   blob+34 u64                  echoed into 2718 blob+34
///   blob+42 u8
///   blob+43 u16                  echoed into 2718 blob+42 (the skill-effect index)
///   blob+45 u16 hitCount
///   blob+47 u32 targetHandle[hitCount]
/// </code>
/// <para>
/// The blob length is <c>47 + 4·u16@45</c>; the count is a u16, not a u8.
/// The full list of actors the skill hit lives at blob+47, and that list is what populates 2718's target
/// vector.
/// </para>
/// <para>
/// The reply is a <b>MAP BROADCAST with the caster INCLUDED</b>: a 2718 with the same skill id also reaches
/// the REQUESTING client. <c>BroadcastToNearbyAsync</c> excludes the source session and would drop
/// exactly that copy, so <c>BroadcastToMapAsync</c> with no exclusion is used.
/// </para>
/// <para>
/// REFUSAL. When the primary target is an NPC that is already dead, the server answers with no 2718.
/// It sends a unicast 2717 AnsSkillError <c>[u16 code][u32 skill]</c> and then a map broadcast of
/// 2963 NfySkillDamageError that names the caster, with code 24310. See
/// <see cref="SkillDamageErrorPackets.ResultPrimaryTargetDead"/>. That is the only refusal this
/// handler makes for a landed skill; other 2717 codes are not guessed at.
/// </para>
/// <para>
/// REVIVAL SKILLS (Skill_Function 7) take their own path before any of the above: their target is a dead
/// PC, not an NPC, and the answer is a fixed-shape 2718 plus a 3016 NfyRevivalSkill offer to that PC.
/// See <see cref="LandRevivalSkillAsync"/>. The guild EMERGENCY CALL (Skill_Function 28) does too: a
/// self-shaped 2718 and the 2960 prompt to the rest of the guild, see <see cref="LandEmergencyCallAsync"/>.
/// </para>
/// <para>
/// THE ECHOES: 2718 blob+21..32 equals the request's blob+9..20 (not the request's +21 direction; request +9 is
/// zero when there is a primary target and those 2718s carry zeros), 2718's u64 at +34 equals
/// the request's u64 at +34, and 2718's u16 at +42 equals the request's u16 at +43 (not a constant).
/// castType (+33) is echoed as well.
/// </para>
/// <para>
/// ELEMENTS BY SKILL TYPE (see <see cref="SkillDamagePcElements"/>): an attack skill (Skill_Type 1)
/// damages the monsters <see cref="SelectNpcVictims"/> picks from the ones it names, each with
/// max(1, Number_Of_Normal_Influences) damage records that share its DamageRate (<see cref="SkillLevelColumns"/>; a
/// Detection skill lists the monsters with no record and damages nothing), and a PC only as the caster's
/// opponent in a started field duel
/// (<see cref="LandPvpHitAsync"/>). A buff (4) lists every named PC, the caster included, with its HP and SP and no
/// effect record, and no monster. A debuff (2) lists the named PCs other than the caster the same way, and each
/// named monster with its vitals, undamaged. A type-10 cast at one PC sends a single all-zero element. A heal (3)
/// lists nothing: there is no heal model. A skill whose type is unknown keeps the attack rule for monsters.
/// </para>
/// <para>
/// SIDE EFFECTS around every 2718 this handler sends: for each monster it killed, the 2643 death panel, the
/// kill's credit (LootService.ProcessKillCreditAsync: 2425/2427 quest ticks, 2577 Khara progress, the EXP
/// grant with its 2394 burst and each receiver's 2393) and the 2471 loot grant, all before it; a reset-all
/// skill's 2817 burst right before it (SkillCoolTimeReset); after it the caster's armour wear 2503s when the 2718
/// lists the caster, its weapon wear 2503s, the armour wear of every other PC it lists (to each one's own session,
/// DurabilityService.SkillWearPcHandles), then the state-3 2398 of each monster the hit engages
/// (NpcChatService.EnterCombatAsync), then each killed monster's corpse (LootService.ProcessNpcCorpseAsync, which
/// sends the 2392). That is the order for a skill kill.
/// </para>
/// </remarks>
public class SkillDamageHandler : ZoneRmiHandlerBase<SkillDamageHandler>
{
    public override ushort Opcode => ZoneC2SOpcodes.ReqSkillDamage;
    public override string Name => "SkillDamage";

    /// <summary>Fixed part of the 1509 blob, up to and including the u16 hit count.</summary>
    private const int RequestHeadLen = 47;

    /// <summary>Size of the u32 blob-length prefix that precedes the 1509 blob.</summary>
    private const int BlobLenPrefixSize = 4;

    /// <summary>Size of the cast direction (three f32) at blob+21, which 2718 does not echo.</summary>
    private const int CastDirectionSize = 12;

    /// <summary>Size of one u32 actor handle in the hit list at blob+47.</summary>
    private const int TargetHandleSize = 4;

    /// <summary>Skill ids are <c>group * SkillGroupDivisor + level</c>: id / 100 is the skill group, id % 100 the cast level.</summary>
    private const uint SkillGroupDivisor = 100u;

    /// <summary>Mask of the actor type tag, the low byte of an actor handle (<see cref="PlayerStateManager.PcActorTag"/> for a PC).</summary>
    private const uint ActorTagMask = 0xFF;

    /// <summary>
    /// Cap on the actors one skill may be reported to have hit. This is not a policy number: the
    /// client parses 2718 into a fixed 4,146-byte stack buffer with a 205-byte element stride and no
    /// clamp, so a 21st element would overrun it. See
    /// <see cref="SkillDamagePackets.MaxSkillDamageTargets"/>.
    /// </summary>
    private const int MaxHits = SkillDamagePackets.MaxSkillDamageTargets;

    public SkillDamageHandler(IServiceScopeFactory s, ILogger<SkillDamageHandler> l, PlayerStateManager pm, BroadcastService bc) : base(s, l, pm, bc) { }

    /// <summary>The fields of a 1509 this handler uses, read from <c>[u32 blobLen][blob]</c> (class remarks).</summary>
    internal readonly record struct SkillDamageRequest(uint SkillId, byte HasTarget, uint PrimaryTarget,
        float X, float Y, float Z, byte CastType, ulong Unknown34, ushort SkillEffectIndex, uint[] Targets);

    /// <summary>
    /// Parses a 1509 payload (at least 4 + 47 bytes; the caller checks). <see cref="SkillDamageRequest.Targets"/> is the
    /// primary target (when blob+4 is 1) followed by the hit list, de-duplicated, at most <see cref="MaxHits"/> in all.
    /// </summary>
    internal static SkillDamageRequest ParseRequest(ReadOnlySpan<byte> payload)
    {
        var blob = payload[BlobLenPrefixSize..];
        var reader = new PacketReader(blob);
        var skillId = reader.ReadUInt32();          // blob+0
        var hasTarget = reader.ReadByte();          // blob+4  1 iff a single primary target follows
        var primaryTarget = reader.ReadUInt32();    // blob+5
        var posX = reader.ReadSingle();             // blob+9  the position 2718 echoes (zero with a target)
        var posY = reader.ReadSingle();             // blob+13
        var posZ = reader.ReadSingle();             // blob+17
        reader.Skip(CastDirectionSize);              // blob+21 the cast direction, not echoed
        var castType = reader.ReadByte();           // blob+33
        var unknown34 = reader.ReadUInt64();        // blob+34 echoed into 2718 +34
        reader.Skip(1);                             // blob+42
        var skillEffectIndex = reader.ReadUInt16(); // blob+43 echoed into 2718 +42
        // blob+45 is the hit count. It is read at exactly this offset: the high byte of the +43 index
        // must not be mistaken for part of the count.
        var hitCount = reader.ReadUInt16();

        // The count is the client's, so it is bounded by what the body actually contains AND by
        // the client's own parse capacity before it can size an allocation.
        var available = Math.Max(0, (blob.Length - RequestHeadLen) / TargetHandleSize);
        var hits = Math.Min(Math.Min((int)hitCount, available), MaxHits);

        // SINGLE-TARGET vs AoE. Most 1509 frames are single-target: hitCount==0 with
        // the actual victim in the blob+5 primaryTarget field (hasTarget==1). AoE skills instead fill
        // the blob+47 hit list. Damaging only the list would leave every single-target skill and
        // basic-target skill dealing zero damage. Targets is the NAMED set: primaryTarget
        // (when present) then the hit list, de-duplicated so an AoE that also lists its primary target is not
        // double-hit. Which named monsters an attack skill actually damages is SelectNpcVictims' rule, not this
        // list's (only a subset is damaged).
        var effective = new List<uint>(hits + 1);
        if (hasTarget != 0 && primaryTarget != 0)
            effective.Add(primaryTarget);
        // At most MaxHits actors in all, the primary included: each can become a 2718 element, and the client's
        // stack buffer holds 20.
        for (var i = 0; i < hits && effective.Count < MaxHits; i++)
        {
            var h = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                blob[(RequestHeadLen + TargetHandleSize * i)..]);
            if (!effective.Contains(h))
                effective.Add(h);
        }
        return new SkillDamageRequest(skillId, hasTarget, primaryTarget, posX, posY, posZ, castType, unknown34,
            skillEffectIndex, effective.ToArray());
    }

    protected override async Task HandleAsync(ProudSession session, ReadOnlyMemory<byte> payload, PlayerState player, IServiceProvider services, CancellationToken ct)
    {
        // [u32 blobLen][blob]; the blob's fixed part is 47 bytes.
        if (payload.Length < BlobLenPrefixSize + RequestHeadLen) return;

        // Everything is pulled out of the span BEFORE the first await: a ReadOnlySpan cannot live
        // across one.
        var request = ParseRequest(payload.Span);
        var (skillId, hasTarget, primaryTarget) = (request.SkillId, request.HasTarget, request.PrimaryTarget);
        var (posX, posY, posZ) = (request.X, request.Y, request.Z);
        var (castType, unknown34, skillEffectIndex) = (request.CastType, request.Unknown34, request.SkillEffectIndex);
        var targetHandles = request.Targets;

        // Every 1509 lands its group's cast, before any branch below: a burst duplicate 1508 that comes after it is
        // refused with 20152, one that comes before it gets no answer (see SkillCastLandings).
        SkillCastLandings.MarkLanded(player, skillId);

        // The warp items (ItemWarpSkills). The Butterfly and Dungeon Wings move the player at their 1561's answer, and
        // their 1509 is answered with nothing (no 2718). A town warp moves the player HERE, when the 1509 its 1561
        // answer granted lands: after the 2718 and its wear onto another map, or in place BEFORE the 2718 onto this
        // one (both below).
        // These non-damage functions return before the damage gate below, so each is governed here by
        // the same pending-cast + actor checks (GovernFunctionCastAsync): a forged 1509 with no accepted 1508/1561
        // behind it is refused with the silent 2715 (20152) and does nothing. A warp item is a self cast.
        var itemWarp = ItemWarpSkills.KindOf(skillId);
        if (itemWarp != ItemWarpSkills.MoveKind.None
            && !await GovernFunctionCastAsync(session, player, skillId, null, targeted: false, targetMustBeAlive: true,
                primaryTarget, posX, posY, posZ))
            return;
        if (itemWarp == ItemWarpSkills.MoveKind.OnAnswer)
            return;
        ItemWarpSkills.Destination? landingMove = null;
        if (itemWarp == ItemWarpSkills.MoveKind.OnLanding
            && ItemWarpSkills.TakeLanding(player, skillId, player.MapId, DateTime.UtcNow))
        {
            var warpData = services.GetRequiredService<GameDataStore>();
            landingMove = ItemWarpSkills.TownWarpDestination(skillId, warpData.GetMap, warpData.Positions);
            if (landingMove is null)
                Logger.LogWarning("{Name}: town warp {SkillId} has no loaded destination; not moved", player.Name, skillId);
        }

        // A Skill_Function 7 skill revives a dead PC instead of damaging anything: its own 2718 shape and the
        // 3016 offer to the target (LandRevivalSkillAsync).
        if (RevivalSkillPackets.IsRevivalSkill(skillId))
        {
            // A revival targets a DEAD PC on the caster's map, so the gate's target-alive rule is off for it
            // (GateContext.TargetMustBeAlive = false); a target that does not resolve there is refused.
            var revivalHandle = hasTarget != 0 ? primaryTarget : 0u;
            var revivalTarget = revivalHandle == 0
                ? null
                : PlayerManager.GetPlayersOnMap(player.MapId).FirstOrDefault(p => p.EntityId == revivalHandle);
            if (!await GovernFunctionCastAsync(session, player, skillId,
                    revivalTarget is null ? null : ActorGate.TargetOf(revivalTarget), targeted: true,
                    targetMustBeAlive: false, primaryTarget, posX, posY, posZ))
                return;
            await LandRevivalSkillAsync(player, skillId, revivalHandle, revivalTarget, castType, services, ct);
            await WearWeaponsAsync(session, player, services, ct);
            await KharaService.OnSkillUsedAsync(session, player, skillId, services, Logger, ct);
            return;
        }
        if (RevivalSkillPackets.SkillTableError is { } tableError && !_revivalTableErrorLogged)
        {
            _revivalTableErrorLogged = true;
            Logger.LogWarning("Revival skills are disabled: {Error}", tableError);
        }

        // A Skill_Function 28 skill (the guild Emergency Call) damages nothing either: its self-shaped 2718,
        // then the 2960 prompt to the rest of the guild (LandEmergencyCallAsync).
        if (GuildEmergencyCallService.IsEmergencyCallSkill(skillId))
        {
            // A self cast: no target to resolve, the caster must be alive and the cast must be one it started.
            if (!await GovernFunctionCastAsync(session, player, skillId, null, targeted: false, targetMustBeAlive: true,
                    primaryTarget, posX, posY, posZ))
                return;
            await LandEmergencyCallAsync(player, skillId, primaryTarget, castType, services);
            await WearWeaponsAsync(session, player, services, ct);
            await KharaService.OnSkillUsedAsync(session, player, skillId, services, Logger, ct);
            return;
        }
        if (GuildEmergencyCallService.SkillTableError is { } callTableError && !_emergencyCallTableErrorLogged)
        {
            _emergencyCallTableErrorLogged = true;
            Logger.LogWarning("The guild Emergency Call is disabled: {Error}", callTableError);
        }

        var npcState = services.GetRequiredService<NpcStateManager>();

        // A primary target that is a dead NPC refuses the WHOLE request: no 2718 and no
        // damage, not even to the other actors in the hit list. The
        // rule also covers a burst whose earlier hit already killed the target, and another player's
        // kill landing between 1508 and 1509.
        if (hasTarget != 0 && npcState.GetNpc(primaryTarget) is { IsDead: true })
        {
            await RefuseAsync(session, player, skillId, SkillDamageErrorPackets.ResultPrimaryTargetDead);
            return;
        }

        // Before any damage: when the 1509 names an NPC, that NPC must be
        // alive, on this map and within the skill's reach, and the skill must be learned (ActorGate); and the cast
        // must belong to one this caster legitimately started: an accepted 1508 (or 1561) of this exact skill left
        // a pending entry still inside its grace (SkillCastLandings). The ActorGate runs first so a refused one does
        // not spend the pending cast. A forged 1509 with no start, or a cross-map / out-of-range / unlearned one,
        // deals NO damage and is refused with the silent 2715 (20152) used for an illegitimate skill action.
        // The damage below is then computed from the data DamageRate of THIS skillId (CombatService.CalculateBaseDamage
        // keys group=id/100 and level=id%100 off it, no other client field), and the gate matched that skillId to a
        // pending cast, so naming a different high-rate skill cannot inflate it: that 1509 finds no pending and is refused.
        // A town warp (OnLanding) was already governed at the top of this method (an item skill, never in LearnedSkills,
        // so the learned-skill check below would refuse it); its pending cast is spent there.
        if (itemWarp != ItemWarpSkills.MoveKind.OnLanding)
        {
            var gateTarget = hasTarget != 0 && primaryTarget != 0 && npcState.GetNpc(primaryTarget) is { } primaryNpc
                ? ActorGate.TargetOf(primaryNpc)
                : (GateTarget?)null;
            var gateData = services.GetRequiredService<GameDataStore>();
            byte? levelLimit = TalentRules.TryGetSkillLevelLimit(gateData, skillId / SkillGroupDivisor, out var talentMax) ? talentMax : null;
            var gateRefusal = EvaluateDamageGate(ActorGate.ActorOf(player), skillId, gateTarget,
                ActorGate.SkillReach(skillId / SkillGroupDivisor), levelLimit);
            if (gateRefusal != ActorRefusal.None)
            {
                Logger.LogDebug("{Name}: 1509 skill {SkillId} refused by actor gate: {Refusal}", player.Name, skillId, gateRefusal);
                await RefuseUnauthorizedAsync(session, skillId, primaryTarget, posX, posY, posZ);
                return;
            }
            var channelled = SkillChannelingPackets.IsChannelled(skillId);
            if (!SkillCastLandings.TryGovern(player, skillId, DateTime.UtcNow, consume: !channelled, out _))
            {
                Logger.LogDebug("{Name}: 1509 skill {SkillId} refused, no pending cast within grace", player.Name, skillId);
                await RefuseUnauthorizedAsync(session, skillId, primaryTarget, posX, posY, posZ);
                return;
            }
            // A fresh, accepted 1509 starts a new tick run for this skill: the 1549 ledger restarts here and
            // only here, so a refused / forged 1509 cannot be used to reset it.
            SkillCastLandings.ResetTicks(player, skillId);
        }

        var combat = services.GetRequiredService<CombatService>();
        var npcChat = services.GetRequiredService<NpcChatService>();

        var damaged = new List<SkillDamageHit>(targetHandles.Length);
        var survivors = new List<NpcRuntimeState>(targetHandles.Length);
        // The handle is kept as it was hit: the corpse keeps it even once the NPC respawns under a fresh one.
        var killed = new List<(NpcRuntimeState Npc, uint Handle)>();

        // A PC in the hit list takes damage only as the caster's opponent in a started field duel, and only
        // from an attack skill (Skill_Table_Base.Skill_Type 1, SkillDamagePackets.IsDamageTickSkill): a
        // support skill's "hit" on a PC is a heal or a buff, which this server does not model (the same gate the
        // 1549 tick applies). A buff or debuff instead lists the PC with its vitals, and only an attack skill (or
        // one of unknown type) damages a monster (SkillDamagePcElements, class remarks).
        var attackSkill = SkillDamagePackets.IsDamageTickSkill(skillId);
        var skillType = SkillDamagePackets.SkillTypeOf(skillId);

        // A type-10 cast whose request names one PC: the 2718 carries a single all-zero element.
        var singleZero = SkillDamagePcElements.IsSingleZeroElementCast(skillType, targetHandles);
        if (singleZero)
            damaged.Add(SkillDamagePcElements.SingleZeroElement());

        // Which of the named monsters an attack skill damages (see SelectNpcVictims): the primary
        // alone when it is an NPC, none when the primary is another PC, else the live hit-list monsters up to
        // Skill_Damage_Quantity.
        var npcVictims = SkillDamagePcElements.DamagesNpcs(skillType)
            ? SelectNpcVictims(request, player.EntityId, npcState.GetNpc, SkillLevelColumns.DamageQuantityOf(skillId))
                .ToHashSet()
            : null;
        // max(1, Number_Of_Normal_Influences) damage records per NPC element (SkillLevelColumns).
        var records = SkillLevelColumns.NormalInfluencesOf(skillId);
        if (SkillLevelColumns.TableError is { } levelTableError && !_levelTableErrorLogged)
        {
            _levelTableErrorLogged = true;
            Logger.LogWarning("Skill damage records and quantity limits are off (one record, 20 NPCs): {Error}",
                levelTableError);
        }

        foreach (var targetHandle in singleZero ? Array.Empty<uint>() : targetHandles)
        {
            var npcRuntime = npcState.GetNpc(targetHandle);
            if (npcRuntime is null)
            {
                if (attackSkill && FieldPvPCombat.TryGetStartedOpponent(player, targetHandle, PlayerManager) is { } opponent)
                    damaged.Add(await LandPvpHitAsync(player, opponent, skillId, combat, services, ct));
                else if (SkillDamagePcElements.ResolvePc(PlayerManager, player, targetHandle) is { } pc
                    && SkillDamagePcElements.PcElement(skillType, player, pc) is { } pcElement)
                    damaged.Add(pcElement);
                continue;
            }

            if (!SkillDamagePcElements.DamagesNpcs(skillType))
            {
                // A debuff lists the monster with its vitals and no effect record;
                // buffs, heals and type-10 casts never list one.
                if (SkillDamagePcElements.ListsNpcVitals(skillType))
                    damaged.Add(new SkillDamageHit(targetHandle, (uint)Math.Max(0, npcRuntime.Hp),
                        SkillDamagePackets.NpcTargetSp(npcRuntime)));
                continue;
            }

            // Only monsters (Camp >= 2) take damage, not friendly NPCs.
            if (npcRuntime.Camp <= NpcSpawnService.CampFriendly) continue;

            // A named monster that is not listed: no element, no damage, no threat.
            if (npcVictims is null || !npcVictims.Contains(targetHandle)) continue;

            // A Detection skill (Skill_Table_Base.Influence_Target_Type 9) lists the monster with its vitals and NO
            // damage record, and the monster loses nothing (SkillLevelColumns.DealsNoDamage).
            // No damage means no threat and no engagement either.
            if (SkillLevelColumns.DealsNoDamage(skillId))
            {
                damaged.Add(new SkillDamageHit(targetHandle, (uint)Math.Max(0, npcRuntime.Hp),
                    SkillDamagePackets.NpcTargetSp(npcRuntime)));
                continue;
            }

            // Each record is rolled on its own: an element may mix kind 1 and kind 4 (a crit),
            // and the kind-1 amounts inside one element are equal, which is CombatService's
            // deterministic per-hit damage. The records SHARE the skill's DamageRate: each carries the full hit / N
            // (SkillLevelColumns.RecordDamage). ApplyDamageAsync
            // computes the FULL-rate hit (its own crit roll included), so each record divides its result; giving each
            // record the full hit would deal N times the damage. The sum is taken off the monster's HP once below,
            // which is the reading consistent with the element's post-hit HP.
            var effects = new SkillHitEffect[records];
            var damage = 0;
            for (var r = 0; r < records; r++)
            {
                var (fullHit, kind) = await combat.ApplyDamageAsync(player, targetHandle, skillId, 0);
                var recordDamage = SkillLevelColumns.RecordDamage(fullHit, records);
                effects[r] = new SkillHitEffect(kind, recordDamage);   // kind 1 normal / 4 crit (x2)
                damage += recordDamage;
            }

            // The attacker is passed so the hit puts the caster on the monster's threat table, as a normal
            // hit does: without it a player who fights with skills never becomes the monster's target, and
            // its 2629 list, its 2630s and the 2643 target-of-target leave that player out. The amount is
            // (damage + AddAggro) x stance (ThreatAmount): the element's records summed, one AddAggro of
            // the cast's skill level per application, times the caster's trait-49 StanceAggroRate. A direct skill
            // steps by its damage + 1; some stance skills (e.g. Aura Armor 1104) step by (d + 1) x 4 or
            // x 4.5. The x4 / x4.5 is NOT MODELLED: no skill cast here applies
            // its self-buff, so Aura Armor never reaches ActiveEffects, the stance rate is 0 and those steps bank
            // (d + 1) x1. Also not modelled (ThreatAmount remarks): two-record skills such as 4101/3103 bank only
            // their first record + 1, and skills whose damage lands as 2797 ticks (4121, 4313, 4302, 4309, ...) bank
            // only the AddAggro here, while this server deals their damage here.
            // The damage is first multiplied by the level's AddAggroRate when it is 2 or more: 130510 (rate 3) steps by
            // (3d + 1) x4 / x4.5 (ThreatAmount remarks).
            var threat = ThreatAmount.For(services.GetService<EffectService>(), player, damage,
                ThreatAmount.AddAggroOf(skillId), ThreatAmount.AddAggroRateOf(skillId));
            var (_, died) = npcState.ApplyDamage(targetHandle, damage, player.EntityId, player.Name, threat);

            // 2718's element carries the target's POST-hit vitals, so this must follow the damage
            // application. Each 9-byte record is {kind, damage, extra}; kind 1 is the usual
            // value and what its meaning selects is not established, so nothing more is claimed.
            // elem+8 feeds the target's SP on the client, and a non-zero value is expected for an NPC
            // (one value per handle, equal to that handle's 2506/2317 record SP), so the NPC keeps the SP
            // 2506 advertised instead of being zeroed by every hit. See SkillDamagePackets.NpcTargetSp.
            damaged.Add(new SkillDamageHit(
                targetHandle,
                (uint)Math.Max(0, npcRuntime.Hp),
                SkillDamagePackets.NpcTargetSp(npcRuntime),
                effects));

            if (!died)
            {
                survivors.Add(npcRuntime);
                continue;
            }
            killed.Add((npcRuntime, targetHandle));
        }

        var loot = killed.Count > 0 ? services.GetRequiredService<LootService>() : null;
        if (loot is not null)
        {
            AutoAttackState.Disengage(player);

            // A skill kill's order differs from a normal kill's: the death panel, the kill's credit
            // and the loot grant go BEFORE the 2718, and the 2392 after it. The 2643 precedes the 2471; the 2425 quest
            // tick, the 2427 and the 2577 Khara progress come after the 2643; none of 2425, 2427, 2577 or 2394 falls
            // between the 2718 and the corpse's 2392, and that 2392 comes after the 2718 (for example
            // 2494/2497 -> 2986 -> 2425 -> 2427 -> 2718 -> 2503 -> 2392). The order inside the credit, and its 2393 right
            // before the 2471, are set in LootService.ProcessKillCreditAsync. This server sends no 2986
            // (it keeps no job-mastery points). The 2494/2497 around the kill are its effects ending, which
            // EffectService owns.
            foreach (var (npc, handle) in killed)
            {
                // The death panel: everyone who has the monster selected gets its final 2643, HP 0 and no
                // target (the kill cleared its threat table). NormalAttackDamageHandler pushes the same panel for the
                // same reason.
                await ReqActorDetailInfoHandler.PushIconToWatchersAsync(Broadcast, PlayerManager,
                    npc.MapId, handle, ReqActorDetailInfoHandler.BuildIconOf(npc), ct);

                // The kill's credit, which names no corpse: 2425/2427, 2577, the EXP grant's 2394 burst and
                // each receiver's 2393.
                await loot.ProcessKillCreditAsync(session, player, handle, npc.NpcId);

                // Same grant the normal-attack path sends, for the same reasons: unicast to the killer, and
                // BEFORE the corpse's 2392 (ProcessNpcCorpseAsync below). Without it a skill kill leaves the
                // corpse out of the client's loot list, so CLootIndicatorMgr never lights it and the body
                // cannot be opened.
                await session.SendInternalRmiAsync(ZoneS2COpcodes.OnLootingAuthority,
                    CombatPackets.BuildLootingAuthority2471(handle));
            }
        }

        // A skill that resets every cooldown (4312 Dirty Plan, and 3230 / 5304 by their description)
        // sends its 2817 burst right BEFORE its 2718. See SkillCoolTimeReset.ResetAllAsync.
        if (SkillCoolTimeReset.IsResetAllSkill(skillId))
        {
            var cleared = await SkillCoolTimeReset.ResetAllAsync(session, player, skillId, DateTime.UtcNow);
            Logger.LogDebug("{Name}: skill {SkillId} reset {Count} cooldown group(s)", player.Name, skillId, cleared);
        }
        else if (SkillCoolTimeReset.TableError is { } resetTableError && !_resetTableErrorLogged)
        {
            _resetTableErrorLogged = true;
            Logger.LogWarning("Cooldown-reset skills are disabled: {Error}", resetTableError);
        }

        // The actors this 2718 carries are the ones the skill's 1549 ticks hit next (SkillDamageTickHandler):
        // the tick set equals the set of the 2718 answering the caster's latest 1509 of the same skill id
        // (see SkillTickTargets). Recorded for the skills whose ticks
        // this server answers, replacing the previous cast's; an empty vector records an empty set.
        if (SkillDamagePackets.IsDamageTickSkill(skillId))
            SkillTickTargets.Record(player, player.MapId, skillId, damaged.Select(h => h.TargetHandle));

        // A town warp onto the caster's own map moves it in place FIRST and sends the 2718 after: the same-map
        // Prontera warp is self 2505 2311 2472 2523 2522, then 2718 2503 x6 (see ItemWarpSkills).
        if (landingMove is { } inPlace
            && ItemWarpSkills.IsInPlace(player, inPlace, services.GetRequiredService<GameDataStore>()))
        {
            await ItemWarpSkills.MoveAsync(session, player, inPlace, services, Logger, ct);
            landingMove = null;
        }

        // The cast's 2630 threat steps go out NOW, as the frames right before the 2718 (after the kill credit, the 2817
        // reset and the in-place warp, so the steps stay next to it), in the same millisecond
        // (NpcCombatService.FlushAggroChangesAsync). A monster this cast killed gets none (the flush drops a dead
        // monster's steps). The 2643 target changes go out AFTER the 2718
        // (PushPendingTargetChangesAsync below): a first-hit acquisition 2643 follows the damage frame, and
        // never goes between the step and the frame. A container without the service leaves both to the pass.
        var npcCombat = services.GetService<NpcCombatService>();
        if (npcCombat is not null)
            await npcCombat.FlushAggroStepsAsync(ct);

        // Broadcast even when nothing resolved: a 2718 with an empty target vector is valid, and
        // the message also drives the cast's visual effect.
        // x/y/z, +34 and +42 are the request's own (+9..20, +34, +43) (class remarks).
        var notify = SkillDamagePackets.BuildSkillDamage2718(
            casterHandle: player.EntityId,
            skillId: skillId,
            primaryTargetHandle: primaryTarget,
            hits: damaged,
            x: posX, y: posY, z: posZ,
            castType: castType,
            skillEffectIndex: skillEffectIndex,
            unknown34: unknown34);

        await Broadcast.BroadcastToMapAsync(player.MapId, ZoneS2COpcodes.NfySkillDamage,
            writer => writer.WriteBytes(notify));
        if (npcCombat is not null)
            await npcCombat.PushPendingTargetChangesAsync(ct);

        // Armour wear of every PC this 2718 lists, and of the one PC a type-10 single-zero cast named
        // (DurabilityService.SkillWearPcHandles). Unlisted PCs get none.
        // The caster's goes out FIRST, before its weapon wear: the first frame after its 2718 is the armour 2503.
        // Each PC is told on its own session. Not covered: healed PCs (no heal model, so a type-3 2718 lists nobody
        // here) and monster skill casts (no NPC skill casting).
        var durability = services.GetRequiredService<DurabilityService>();
        var wornPcs = DurabilityService.SkillWearPcHandles(damaged, singleZero, targetHandles);
        if (wornPcs.Contains(player.EntityId))
            await durability.ApplyArmourWearOnTakenHitAsync(player, ct);

        await WearWeaponsAsync(session, player, services, ct);

        foreach (var handle in wornPcs)
        {
            if (handle != player.EntityId
                && SkillDamagePcElements.ResolvePc(PlayerManager, player, handle) is { } listedPc)
                await durability.ApplyArmourWearOnTakenHitAsync(listedPc, ct);
        }

        // A landed skill counts for the caster's "Use skill @1" Kharas (Complete_Type 31, skill group = id / 100),
        // after the 2718 and its wear 2503s (2492 -> 2718 -> 2503 x6 -> 2577; KharaService.OnSkillUsedAsync). The
        // revival and emergency-call landings above count the same way.
        await KharaService.OnSkillUsedAsync(session, player, skillId, services, Logger, ct);

        // A skill hit opens a fight like a normal hit does: the monster's state-3 2398 and its situation-02
        // line (NpcChatService.EnterCombatAsync sends both, and only when this hit starts the engagement).
        // After the skill's own 2503s (see EnterCombatAsync). Only for the monsters that survived: after a skill
        // kill no 2398 of the corpse is sent at all, so a monster
        // killed in this hit gets neither a state 3 nor the state-1 leave-combat the normal-kill path sends.
        foreach (var npc in survivors)
            await npcChat.EnterCombatAsync(npc, player.EntityId);

        // Each killed monster's corpse, after the 2718 and its 2503s: the 2392 (see the kill pass above), the drops
        // left on the corpse and its deferred 2507. The credit went out before the 2718.
        if (loot is not null)
        {
            foreach (var (npc, handle) in killed)
            {
                // Close the engagement now rather than at the next NpcChatService sweep. The respawned monster
                // comes back under a fresh handle, which starts with no engagement.
                npcChat.OnNpcLeftCombat(handle);
                await loot.ProcessNpcCorpseAsync(session, player, handle, npc.NpcId);
            }
        }

        // A town warp onto another map, or out of an instance: the departure after the 2718 and its wear
        // (ItemWarpSkills).
        if (landingMove is { } departure)
            await ItemWarpSkills.MoveAsync(session, player, departure, services, Logger, ct);
    }

    /// <summary>
    /// The monsters an attack skill's 1509 damages, in the order the request names them: when the primary target is
    /// an NPC, that NPC alone; when it is a PC other than the caster, none; otherwise (a ground cast, or the caster as
    /// primary) the named NPCs that are alive and hostile (Camp &gt;= 2), de-duplicated, at most
    /// min(20, <c>damageQuantity</c>) of them.
    /// </summary>
    /// <param name="request">The parsed 1509 (<see cref="SkillDamageRequest.Targets"/> is the primary, then the hit list).</param>
    /// <param name="casterHandle">The caster's own handle.</param>
    /// <param name="npcOf">The NPC lookup (NpcStateManager.GetNpc on the live path).</param>
    /// <param name="damageQuantity">max(1, Skill_Damage_Quantity) of the skill (SkillLevelColumns.DamageQuantityOf),
    /// or null for a skill the table does not know (only the 20-element cap).</param>
    /// <remarks>
    /// <para>
    /// (1) The primary is an NPC: only the primary is listed.
    /// (2) The primary is ANOTHER PC (a 0x11 handle that is not the caster): no NPC is listed.
    /// (3) Ground and self-centred casts are real AoE, but only a subset of the hit list is listed, so the hit
    /// list is kept with two limits: never a DEAD NPC (a corpse would get a 0-damage element), and never more
    /// than Skill_Damage_Quantity NPCs. Friendly NPCs (Camp 1) are left out and do not count against the limit.
    /// </para>
    /// </remarks>
    /// <summary>
    /// The 1509 pre-damage precondition: when the cast names an NPC primary target
    /// (<paramref name="npcTarget"/> non-null), it must pass <see cref="ActorGate.CanAct"/>: alive, same map,
    /// within <paramref name="reach"/> (<see cref="float.PositiveInfinity"/> for a Skill_Range-100 skill, so the
    /// range check is skipped) and the skill learned. A ground / self AoE names no NPC primary
    /// (<paramref name="npcTarget"/> null), and only the learned-skill check applies there (a PC primary is left to
    /// the field-duel path, which gates PC damage itself). A class skill's cast level (id % 100) must also lie in
    /// [1, the learned level of its group] and not above <paramref name="maxSkillLevel"/> (the group's
    /// Talent_Table.Max_Skill_Level, null when it has none): the level is the client's, and CombatService prices the
    /// hit off it. Returns <see cref="ActorRefusal.None"/> when the cast may
    /// proceed. Pure: the caller resolves the target and reach so this is unit-testable.
    /// </summary>
    internal static ActorRefusal EvaluateDamageGate(in GateActor actor, uint skillId, in GateTarget? npcTarget,
        float? reach, byte? maxSkillLevel = null)
    {
        // Item skills are validated by POSSESSION, not by a learned check. An item skill
        // (>= FirstNonClassSkillId) is never in LearnedSkills, so a learned check would refuse a legitimate damaging
        // item skill (item held, cast through the 1561 path that already verified ownership and consumed the item,
        // leaving the pending cast that TryGovern below matches). Skip the learned check for it, exactly as
        // EvaluateFunctionGate does; only a class skill is learned-gated. The possession proof is the pending cast
        // the 1561 path recorded: a forged item-skill 1509 with no such cast finds none at TryGovern and is refused.
        uint? learnedGroup = skillId < FirstNonClassSkillId ? skillId / SkillGroupDivisor : null;
        var refusal = npcTarget is { } target
            ? ActorGate.CanAct(actor, target, new GateContext(reach, SkillGroup: learnedGroup))
            : learnedGroup is not null
                ? (actor.LearnedSkills is { } known && known.TryGetValue(learnedGroup.Value, out var knownLevel) && knownLevel > 0
                    ? ActorRefusal.None
                    : ActorRefusal.SkillNotLearned)
                : ActorRefusal.None;
        if (refusal != ActorRefusal.None || learnedGroup is not { } group)
            return refusal;

        // The cast level is the id's own (id % 100) and the client's to choose, so it must lie in
        // [1, learned level of the group] and within the group's Max_Skill_Level when the caller has one
        // (TalentRules.TryGetSkillLevelLimit). Learned >= 1 is established above, so the lookup holds.
        var castLevel = skillId % SkillGroupDivisor;
        var learnedLevel = actor.LearnedSkills![group];
        return castLevel >= 1 && castLevel <= learnedLevel && (maxSkillLevel is not { } max || castLevel <= max)
            ? ActorRefusal.None
            : ActorRefusal.SkillNotLearned;
    }

    /// <summary>
    /// Refuses a 1509 that the actor gate rejects or that no accepted cast governs, the way an
    /// illegitimate skill action is refused: a single SILENT 2715 AnsUseSkillStart carrying 20152
    /// (<see cref="SkillPackets.SkillResultOnCooldown"/>), unicast to the caster, with no 2716,
    /// no 2718 and no damage.
    /// </summary>
    private Task RefuseUnauthorizedAsync(ProudSession session, uint skillId, uint targetHandle,
        float x, float y, float z)
        => session.SendInternalRmiAsync(ZoneS2COpcodes.AnsUseSkillStart,
            SkillPackets.BuildUseSkillStartAck2715(skillId, targetHandle, 0f, x, y, z,
                resultCode: SkillPackets.SkillResultOnCooldown));

    /// <summary>First skill id that is not a class skill: the 2,000,000+ ids are item, guild and monster skills, which
    /// are never in <see cref="PlayerState.LearnedSkills"/> (SkillStartHandler uses the same boundary).</summary>
    internal const uint FirstNonClassSkillId = 2_000_000u;

    /// <summary>
    /// The pre-effect precondition of the non-damage 1509 functions (revival, emergency call, the warp
    /// items): the actor must be alive; a <paramref name="targeted"/> cast needs its resolved target on the actor's
    /// map within the skill's <paramref name="reach"/> (alive only when <paramref name="targetMustBeAlive"/>, which
    /// a revival turns off since it raises a dead PC); and a class skill (below <see cref="FirstNonClassSkillId"/>)
    /// must be learned. An item or guild skill is vouched for elsewhere (the 1561's item check, the guild
    /// validation), so it skips the learned check. Pure, like <see cref="EvaluateDamageGate"/>.
    /// </summary>
    internal static ActorRefusal EvaluateFunctionGate(in GateActor actor, uint skillId, in GateTarget? target,
        bool targeted, float? reach, bool targetMustBeAlive)
    {
        uint? learnedGroup = skillId < FirstNonClassSkillId ? skillId / SkillGroupDivisor : null;
        if (targeted)
            return ActorGate.CanAct(actor, target,
                new GateContext(reach, SkillGroup: learnedGroup, TargetMustBeAlive: targetMustBeAlive));
        if (actor.IsDead) return ActorRefusal.ActorDead;
        return learnedGroup is { } group
            && !(actor.LearnedSkills is { } learned && learned.TryGetValue(group, out var level) && level > 0)
            ? ActorRefusal.SkillNotLearned
            : ActorRefusal.None;
    }

    /// <summary>
    /// Runs <see cref="EvaluateFunctionGate"/>, then spends the governing pending cast (<see cref="SkillCastLandings.TryGovern"/>,
    /// as the damage path does, the actor gate first so a refused one does not spend it). True when the function may
    /// take effect; otherwise the caster has been answered with the silent 2715 (20152) and nothing else is sent.
    /// </summary>
    private Task<bool> GovernFunctionCastAsync(ProudSession session, PlayerState player, uint skillId,
        GateTarget? target, bool targeted, bool targetMustBeAlive, uint primaryTarget, float x, float y, float z)
        => GovernFunctionCastAsync(session, player, skillId, target, targeted, targetMustBeAlive,
            ActorGate.SkillReach(skillId / SkillGroupDivisor), !SkillChannelingPackets.IsChannelled(skillId), primaryTarget, x, y, z);

    /// <summary>As the overload above with the skill's table-derived <paramref name="reach"/> and <paramref name="consume"/>
    /// given (the table lookups are the caller's, so this is exercisable without the shipped tables).</summary>
    internal async Task<bool> GovernFunctionCastAsync(ProudSession session, PlayerState player, uint skillId,
        GateTarget? target, bool targeted, bool targetMustBeAlive, float? reach, bool consume, uint primaryTarget,
        float x, float y, float z)
    {
        var refusal = EvaluateFunctionGate(ActorGate.ActorOf(player), skillId, target, targeted, reach, targetMustBeAlive);
        if (refusal != ActorRefusal.None)
        {
            Logger.LogDebug("{Name}: 1509 skill {SkillId} refused by actor gate: {Refusal}", player.Name, skillId, refusal);
        }
        else if (!SkillCastLandings.TryGovern(player, skillId, DateTime.UtcNow, consume, out _))
        {
            Logger.LogDebug("{Name}: 1509 skill {SkillId} refused, no pending cast within grace", player.Name, skillId);
        }
        else
        {
            return true;
        }
        await RefuseUnauthorizedAsync(session, skillId, primaryTarget, x, y, z);
        return false;
    }

    internal static List<uint> SelectNpcVictims(SkillDamageRequest request, uint casterHandle,
        Func<uint, NpcRuntimeState?> npcOf, int? damageQuantity)
    {
        var victims = new List<uint>();
        if (request.HasTarget != 0 && request.PrimaryTarget != 0)
        {
            if (npcOf(request.PrimaryTarget) is not null)
            {
                victims.Add(request.PrimaryTarget);
                return victims;
            }
            if ((request.PrimaryTarget & ActorTagMask) == PlayerStateManager.PcActorTag && request.PrimaryTarget != casterHandle)
                return victims;
        }

        var cap = Math.Min(MaxHits, damageQuantity ?? MaxHits);
        foreach (var handle in request.Targets)
        {
            if (victims.Count >= cap) break;
            if (npcOf(handle) is not { } npc || npc.IsDead || npc.EntityOid != handle || npc.Camp <= NpcSpawnService.CampFriendly) continue;
            if (!victims.Contains(handle)) victims.Add(handle);
        }
        return victims;
    }

    /// <summary>
    /// One skill hit on the caster's started-duel opponent (FieldPvPCombat): the damage resolved against the
    /// victim (CombatService.ResolvePvpHit), taken off its HP, and the 2718 element that reports it, with the
    /// victim's HP after the hit and its SP. A hit that would take it to 0 leaves it at HP 1 and ends the duel as
    /// fought out right here, i.e. BEFORE the 2718 this handler broadcasts after its loop: the
    /// 2736 and its companions first and the lethal 2718 (with HP 1) after them. The element's handle joins the
    /// ones SkillTickTargets records, so the skill's 1549
    /// ticks hit the opponent too.
    /// </summary>
    private async Task<SkillDamageHit> LandPvpHitAsync(PlayerState caster, FieldPvPCombat.Opponent opponent,
        uint skillId, CombatService combat, IServiceProvider services, CancellationToken ct)
    {
        var victim = opponent.Victim;
        var (damage, kind) = combat.ResolvePvpHit(caster, victim, skillId);
        if (FieldPvPCombat.TakeHit(victim, damage))
        {
            await FieldPvPCombat.EndFoughtOutAsync(opponent.Duel, caster, victim, services, Broadcast, ct);
            Logger.LogInformation("{Name} won duel {Id} against {Other} with skill {SkillId}",
                caster.Name, opponent.Duel.Id, victim.Name, skillId);
        }
        return new SkillDamageHit(
            victim.EntityId,
            (uint)Math.Max(0, victim.Hp),
            (uint)Math.Clamp(victim.Mp, 0, ushort.MaxValue),
            new[] { new SkillHitEffect(kind, damage) });
    }

    /// <summary>
    /// Weapon durability wear after the caster's own 2718, one 2503 per worn weapon-slot item, unicast to the
    /// caster. Wear happens after every skill use, damaging or not (see DurabilityService), and the
    /// guild Emergency Call's self-shaped 2718 is followed by one too. A refused 1509 sends no 2718 and
    /// wears nothing.
    /// </summary>
    private static Task WearWeaponsAsync(ProudSession session, PlayerState player, IServiceProvider services,
        CancellationToken ct)
        => services.GetRequiredService<DurabilityService>().ApplyWeaponWearOnHitAsync(session, player, services, ct);

    /// <summary>
    /// The landing of the guild Emergency Call (Skill_Function 28, 4000101..4000103): its self-shaped 2718 to
    /// the map, and when the caster is the guild's master with the skill learned on a Targeting_Map_ID map,
    /// the call and its 2960 prompt to every other online member (GuildEmergencyCallService).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The 2718 is a fixed 48-byte body:
    /// primary target = the 1509's own (the caster), no position, no hit element, effect index 1. The xyz is
    /// 0 although the 1509 carries a direction at blob+21, for example direction (-0.067, 0.998) gives
    /// a zero xyz, with the effect index 1 at blob+42.
    /// </para>
    /// <para>
    /// The 2718 goes out whatever the validation says, because it answers the 1509 of a cast this server
    /// already granted with its 2715. A cast that fails the validation issues no call and sends no 2960.
    /// </para>
    /// </remarks>
    private async Task LandEmergencyCallAsync(PlayerState caster, uint skillId, uint primaryTarget, byte castType,
        IServiceProvider services)
    {
        var notify = SkillDamagePackets.BuildSkillDamage2718(
            casterHandle: caster.EntityId,
            skillId: skillId,
            primaryTargetHandle: primaryTarget,
            hits: null,
            x: 0f, y: 0f, z: 0f,
            castType: castType,
            skillEffectIndex: SkillDamagePackets.SkillEffectIndexPcModal);
        await Broadcast.BroadcastToMapAsync(caster.MapId, ZoneS2COpcodes.NfySkillDamage,
            writer => writer.WriteBytes(notify));

        await GuildService.Instance.InitAsync(services.GetRequiredService<IGuildStore>());
        var guild = GuildService.Instance.GetByCharId(caster.CharacterId);
        var data = services.GetRequiredService<GameDataStore>();
        var refusal = GuildEmergencyCallService.Validate(guild, caster.CharacterId, skillId, caster.MapId,
            GuildEmergencyCallService.TargetMapsOf(skillId), data.GuildSkillLevel);
        if (refusal != GuildEmergencyCallService.CastRefusal.None || guild is null)
        {
            Logger.LogInformation("{Name} landed the guild Emergency Call {SkillId} on map {Map} but issues no call: {Refusal}",
                caster.Name, skillId, caster.MapId, refusal);
            return;
        }

        var prompted = await GuildEmergencyCallService.IssueAsync(guild, caster, PlayerManager, Broadcast, DateTime.UtcNow);
        Logger.LogInformation("{Name} issued the guild Emergency Call {SkillId} for guild {Guild} at map {Map} ({X:F1}, {Y:F1}, {Z:F1}); {Count} member(s) prompted",
            caster.Name, skillId, guild.Id, caster.MapId, caster.X, caster.Y, caster.Z, prompted);
    }

    /// <summary>Set once the missing-table warning of <see cref="RevivalSkillPackets.SkillTableError"/> is logged.</summary>
    private static bool _revivalTableErrorLogged;

    /// <summary>Set once the missing-table warning of <see cref="GuildEmergencyCallService.SkillTableError"/> is logged.</summary>
    private static bool _emergencyCallTableErrorLogged;

    /// <summary>Set once the missing-table warning of <see cref="SkillCoolTimeReset.TableError"/> is logged.</summary>
    private static bool _resetTableErrorLogged;

    /// <summary>Set once the missing-table warning of <see cref="SkillLevelColumns.TableError"/> is logged.</summary>
    private static bool _levelTableErrorLogged;

    /// <summary>
    /// The landing of a revival skill (Skill_Function 7: 9200101 Yggdrasil Leaf, 230301 Soul Bind, 511001
    /// Resurrection, ...): the revival 2718 to the map, and when the target is a dead PC, the 3016
    /// NfyRevivalSkill offer to that PC alone, recorded so its 1684 AnsRevivalSkill can be honoured.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Order: the 2718 (map broadcast, caster included, as for every
    /// 2718), then in the dead PC's stream the 3016, with only the target's armour 2503s between
    /// them (sent here); onlooker and caster copies of revival 2718s are not followed by a 3016. Field
    /// layouts are on
    /// <see cref="RevivalSkillPackets.BuildRevivalSkillDamage2718"/> and
    /// <see cref="RevivalSkillPackets.BuildNfyRevivalSkill3016"/>.
    /// </para>
    /// <para>
    /// The offered position is the caster's server-side position, which ViewGroupMoveHandler keeps at the
    /// last 1526 +40; it equals the 1561 request's +4 position. For the
    /// learned skill (Soul Bind) the target is revived at the 1508's +4 position.
    /// </para>
    /// <para>
    /// Nothing else changes here: no HP, no damage, no NPC, no loot. The revival itself happens when the
    /// target accepts (RevivalSkillHandler). The offer is stored BEFORE the 3016 goes out, so a 1684 that
    /// arrives on the target's session right behind it always finds it.
    /// </para>
    /// </remarks>
    internal async Task LandRevivalSkillAsync(PlayerState caster, uint skillId, uint primaryTarget, PlayerState? target,
        byte castType, IServiceProvider services, CancellationToken ct)
    {
        var offered = RevivalSkillPackets.CanBeOffered(target, caster) ? target : null;

        var notify = RevivalSkillPackets.BuildRevivalSkillDamage2718(
            caster.EntityId, skillId, primaryTarget, offered, castType);
        await Broadcast.BroadcastToMapAsync(caster.MapId, ZoneS2COpcodes.NfySkillDamage,
            writer => writer.WriteBytes(notify));

        if (offered is null)
        {
            Logger.LogDebug("{Name}: revival skill {SkillId} on 0x{Target:X8}, which is not a dead PC here; no offer",
                caster.Name, skillId, primaryTarget);
            return;
        }

        // The dead target is listed in the revival 2718, so its armour wears like any listed PC's
        // (DurabilityService.SkillWearPcHandles): its 5 armour 2503s sit between the 2718 and the 3016.
        // Sent to the target's own session, before the offer goes out.
        await services.GetRequiredService<DurabilityService>().ApplyArmourWearOnTakenHitAsync(offered, ct);

        var offer = new RevivalOffer(caster.EntityId, skillId, caster.X, caster.Y, caster.Z);
        offered.AddRevivalOffer(offer);

        var targetSession = Broadcast.GetSession(offered.SessionId);
        if (targetSession is not null)
        {
            await targetSession.SendInternalRmiAsync(ZoneS2COpcodes.NfyRevivalSkill,
                RevivalSkillPackets.BuildNfyRevivalSkill3016(offer.X, offer.Y, offer.Z, offer.CasterHandle, offer.SkillId), ct);
        }

        Logger.LogDebug("{Name} offers {Target} a revival (skill {SkillId}) at ({X}, {Y}, {Z})",
            caster.Name, offered.Name, skillId, offer.X, offer.Y, offer.Z);
    }

    /// <summary>
    /// Refuses a 1509. First a unicast 2717 goes to the caster, then a 2963 goes
    /// to the whole map, caster included.
    /// </summary>
    /// <remarks>
    /// Order and audience: a 2817 refund precedes the 2717 when the skill is still
    /// cooling (see the body). Each 2717 is followed within
    /// 50 ms by a 2963 with the same code and skill. The 2963 names the caster, and is mostly received by
    /// players other than the caster. After a refusal, no 2718 from the
    /// caster for that skill arrives before the next request.
    /// </remarks>
    private async Task RefuseAsync(ProudSession session, PlayerState player, uint skillId, ushort resultCode)
    {
        // The refused skill's cooldown is handed back first, when it is still running: a 24310
        // refusal of a skill with a cooldown is preceded by a 2817 for its group, 0-20 ms before the 2717. See
        // SkillCoolTimeReset.RefundOnRefusalAsync.
        if (resultCode == SkillDamageErrorPackets.ResultPrimaryTargetDead)
            await SkillCoolTimeReset.RefundOnRefusalAsync(session, player, skillId, DateTime.UtcNow);

        await session.SendInternalRmiAsync(ZoneS2COpcodes.AnsSkillError,
            CombatSkillNotifyPackets.BuildSkillError2717(resultCode, skillId));

        await Broadcast.BroadcastToMapAsync(player.MapId, ZoneS2COpcodes.NfySkillDamageError,
            writer => writer.WriteBytes(SkillDamageErrorPackets.BuildSkillDamageError2963(
                resultCode, skillId, player.EntityId)));
    }
}
