using Microsoft.Extensions.DependencyInjection;
using RO2.Combats.Domain;
using RO2.Skills.Domain;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.GameData;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Inventory.Domain;
using RO2.Characters.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Handles combat calculations: damage, HP changes, death, level up.
/// Uses NpcStateManager for real NPC ATK/DEF from NpcServerInfo.
/// </summary>
public class CombatService
{
    private readonly PlayerStateManager _playerManager;
    private readonly NpcStateManager _npcState;
    private readonly BroadcastService _broadcast;
    private readonly GameDataStore _data;
    private readonly ILogger<CombatService> _logger;

    /// <summary>
    /// Where the level-up burst gets EffectService (the 2498 sheet) and IInventoryRepository (the worn gear) at
    /// run time. Taken lazily instead of as constructor dependencies because EffectService already depends,
    /// through LootService, on this service: EffectService -&gt; LootService -&gt; CombatService -&gt; EffectService
    /// would be a cycle the container refuses at startup. The container always supplies it (every
    /// ServiceProvider registers IServiceScopeFactory); a test that builds this service without one gets the
    /// burst without its 2595/2498, see <see cref="GrantExpAsync"/>.
    /// </summary>
    private readonly IServiceScopeFactory? _scopeFactory;

    public CombatService(
        PlayerStateManager playerManager,
        NpcStateManager npcState,
        BroadcastService broadcast,
        GameDataStore data,
        ILogger<CombatService> logger,
        IServiceScopeFactory? scopeFactory = null)
    {
        _playerManager = playerManager;
        _npcState = npcState;
        _broadcast = broadcast;
        _data = data;
        _logger = logger;
        _scopeFactory = scopeFactory;
    }

    /// <summary>
    /// Apply damage to a target. Returns actual damage dealt.
    /// Uses NpcStateManager to factor in target NPC's DEF from NpcServerInfo.
    /// </summary>
    public async Task<(int Damage, byte Kind)> ApplyDamageAsync(PlayerState attacker, uint targetEntityId, uint skillId, int baseDamage)
    {
        // Calculate base damage from attacker stats + skill
        var rawDamage = baseDamage > 0 ? baseDamage : CalculateBaseDamage(attacker, skillId);

        // Defence MITIGATES proportionally rather than subtracting (AoV DefenseRate):
        // Defense% = 100·Def_eff / (Def_eff + 870 + AttackerLv·35), capped 75%.
        // The attacker's Penetration (trait 94) chews through the target's armour first (Def_eff), a strict
        // no-op at Pen 0. AttackerLv is the player's level; Def is the monster's DEF from NpcServerInfo.
        var npcTarget = _npcState.GetNpc(targetEntityId);

        // A monster that has leashed and is running home (NpcRuntimeState.IsReturning) is invulnerable: the hit
        // MISSES (damageKind 2, 0 damage) and banks no threat (NpcStateManager.ApplyDamage also no-ops it), so it
        // keeps returning instead of being dragged back.
        if (npcTarget is not null && npcTarget.IsReturning(DateTime.UtcNow))
            return (0, CombatPackets.DamageKindMiss);

        int defenderLevel = npcTarget?.Level ?? attacker.Level;
        if (npcTarget != null)
            rawDamage = DefenseRules.Mitigate(rawDamage, npcTarget.Def, attacker.CombatPen, attacker.Level);

        // No random variance: normal-attack damage is deterministic per hit for fixed conditions. Back-to-back
        // same-target swings differ only by slow drift from changing state, not by an independent roll, so no
        // random jitter is applied.
        var damage = Math.Max(1, rawDamage);

        // CRITICAL HIT — the ATTACKER's Crit stat (trait 60) rolls a crit that DOUBLES the final damage and
        // flags the hit as kind 4 (the client renders it in the crit colour): flat ×2.0 on the displayed number.
        // Rate = Crit/(0.02·Crit + 40 + 0.8·(DefLv−AtkLv)), capped 90% (AoV). A naked character's Crit is small
        // (crits rare); gear raises it. NPCs carry no Crit stat, so NPC→player crits do not occur here (that path
        // lives in NpcCombatService).
        byte kind = DamageKindNormal;
        if (Rolls(CombatRateRules.CritRatePct(attacker.CombatCrit, defenderLevel, attacker.Level)))
        {
            damage = Math.Max(1, damage * 2);
            kind = DamageKindCrit;
        }

        // NOT SENT. 2718 NfySkillDamage is for SKILL damage, and broadcasting it on every damage
        // application was wrong twice over: a normal attack's damage is carried by 2696
        // NfyAttackDamage, which AutoAttackService already sends correctly, and this body was raw.
        // Its first byte was the attacker's SessionId, so for session 1 it read as a compact-length
        // marker of 1 with a length of 0 -- an empty ByteArray, hence a NULL blob and a crash.
        //
        // 2718 is variable-length and its layout is not modelled here. Model it when skills are implemented.

        return (damage, kind);
    }

    /// <summary>2696/2718/2797 damage kind of an ordinary hit.</summary>
    internal const byte DamageKindNormal = 1;
    /// <summary>Damage kind of a parried (guarded) hit, x0.5 (see NpcCombatService).</summary>
    internal const byte DamageKindGuard = 3;
    /// <summary>Damage kind of a critical hit, x2.</summary>
    internal const byte DamageKindCrit = 4;

    /// <summary>
    /// Resolves one hit of <paramref name="attacker"/> on another PLAYER, <paramref name="victim"/>, without
    /// applying it: the damage and its kind (1 normal, 3 guard, 4 crit). Used by the field duel
    /// (FieldPvPCombat), whose callers apply the HP change and send the damage message.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The AoV formulas this server already uses, with the victim's side filled from the victim, which
    /// <see cref="ApplyDamageAsync"/> cannot do for a PC (it looks the target up among the NPCs, so a PC target got
    /// no defence and a crit rolled against the attacker's own level):
    /// <list type="bullet">
    /// <item>base damage: <see cref="CalculateBaseDamage"/> (a skill id resolves its DamageRate, 0 is a normal attack);</item>
    /// <item>defence: the victim's effective Defense (PlayerState.CombatDefense, the 2498 sheet's trait 26) through
    /// <see cref="MitigateByDefense"/>, with the attacker's level and Penetration, capped 75%;</item>
    /// <item>crit: the attacker's Crit against the VICTIM's level (<see cref="CombatRateRules.CritRatePct"/>), x2, kind 4;</item>
    /// <item>parry: the victim's Parry against the attacker's level (<see cref="CombatRateRules.ParryRatePct"/>, the NPC-to-PC rule
    /// of NpcCombatService), x0.5, kind 3.</item>
    /// </list>
    /// The original game's PvP hits carry all of these kinds: 4 (crit), 3 (guard), 1 (normal) and 2 (miss). Kind 2,
    /// a miss, has no evasion model on this server, so it is never produced. A hit has one kind, so a crit is not
    /// also parried; that precedence is this server's choice.
    /// </para>
    /// <para>
    /// OPEN: no data table carries a PvP damage modifier (only Map_PK, PvP_Point and Skill_Table_Base's Battle_*
    /// columns exist), so whether PvP damage is scaled is not established; these are the PvE formulas.
    /// </para>
    /// </remarks>
    public (int Damage, byte Kind) ResolvePvpHit(PlayerState attacker, PlayerState victim, uint skillId)
    {
        var damage = DefenseRules.Mitigate(CalculateBaseDamage(attacker, skillId), victim.CombatDefense,
            attacker.CombatPen, attacker.Level);
        if (Rolls(CombatRateRules.CritRatePct(attacker.CombatCrit, victim.Level, attacker.Level)))
            return (Math.Max(1, damage * 2), DamageKindCrit);
        if (Rolls(CombatRateRules.ParryRatePct(victim.CombatParry, victim.Level, attacker.Level)))
            return (Math.Max(1, (int)Math.Round(damage * 0.5)), DamageKindGuard);
        return (damage, DamageKindNormal);
    }

    // MitigateByDefense moved to RO2.Combats.Domain.DefenseRules.Mitigate (Combat context, B4 S2).

    // CritRatePct / ParryRatePct moved to RO2.Combats.Domain.CombatRateRules (Combat context, B4 S3).

    private static bool Rolls(double pct) => pct > 0 && Random.Shared.NextDouble() * 100.0 < pct;

    /// <summary>
    /// Grant EXP to a player. A grant that crosses one or more level boundaries is answered with ONE level-up
    /// burst after the whole grant has been applied: 2595 -&gt; 2498 -&gt; 2394 -&gt; 2395 -&gt; 2542 [-&gt; 2726].
    /// </summary>
    /// <remarks>
    /// <para>
    /// A grant that crosses level boundaries produces a single burst:
    /// <list type="bullet">
    /// <item>The two frames right before the 2394 are 2595 OnPcInitTrait then 2498 OnPcTrait.</item>
    /// <item>That 2498 is the sheet at the NEW level: its max HP/SP (blob+8/+12) equal the 2394's max HP/SP. Its
    /// current HP (blob+72) is the live one from before the level-up, not a refill. The 2595's blob+72/+76 equal
    /// that 2498's current HP/SP, which is what WorldEntryService.BuildPcInitTrait2595 copies.</item>
    /// <item>The 2394 then carries current == max for HP and SP: the level-up refills.</item>
    /// <item>After it come the leveler's OWN 2395, carrying its own handle at blob+8, then one 2542, and 2726 for a
    /// guilded leveler.</item>
    /// <item>ONE burst per grant, however many levels it crosses. The 2394's stat point field is the sum over those
    /// levels (CombatPackets.StatPointsForLevelUp(from, to)).</item>
    /// <item>A multi-level burst carries ONE 2726 whose amount is the LAST completed level's Base_Exp_Next / 100
    /// (not the sum over the levels).</item>
    /// </list>
    /// The 2395 goes to the leveler as well as to the players around.
    /// </para>
    /// <para>
    /// A level-up often follows a quest prize (1378 -&gt; 2429 -&gt; 2725 -&gt; this burst -&gt;
    /// 2435, QuestPrizeHandler.GrantRewardsAsync). Kill EXP (LootService), party shares and the one-click jump
    /// (JumpingLevelupHandler) come through here too and get the same burst.
    /// </para>
    /// </remarks>
    public async Task GrantExpAsync(PlayerState player, ulong expAmount)
    {
        player.Exp += expAmount;

        var session = _broadcast.GetSession(player.SessionId);
        if (session == null) return;

        // NOT SENT. 2371 OnFillupEXP is not sent by the original server in this build and its layout is
        // unknown. A raw body in this family is a client CRASH, not a dropped message: the compact decoder
        // rejects it and the stub hands the consumer a NULL blob it dereferences. Losing a notify beats guessing.

        // The whole grant first: one burst is sent for it, whatever the number of levels crossed.
        int oldLevel = player.Level;
        ulong completedLevelExp = 0;   // Base_Exp_Next of the last level completed, for the 2726
        var expNeeded = (ulong)_data.GetExpForNextLevel(player.Level);
        while (player.Exp >= expNeeded && player.Level < 50)
        {
            player.Exp -= expNeeded;
            player.Level++;
            completedLevelExp = expNeeded;
            expNeeded = (ulong)_data.GetExpForNextLevel(player.Level);
        }
        if (player.Level == oldLevel) return;

        _logger.LogInformation("Player {Name} leveled up {Old} -> {Level}!", player.Name, oldLevel, player.Level);
        await SendLevelUpBurstAsync(session, player, oldLevel, completedLevelExp);
    }

    /// <summary>
    /// The level-up burst for a grant that took <paramref name="player"/> from <paramref name="oldLevel"/> to its
    /// current level, in retail's order (see <see cref="GrantExpAsync"/>).
    /// </summary>
    private async Task SendLevelUpBurstAsync(ProudSession session, PlayerState player, int oldLevel,
        ulong completedLevelExp)
    {
        // 2595 + 2498: the sheet at the new level, built while PlayerState.Hp/Mp still hold the live values from
        // before the level-up. EffectService.RefreshPcTraitSheet also sets the runtime max HP/SP to the sheet's
        // (gear and active effects included) and refreshes the combat cache for the new level. Without the sheet
        // (no scope factory, no character snapshot, or the gear could not be read) the max comes from the
        // base-stat formula as before, and neither message goes out: a sheet built without the worn gear would
        // show every gear bonus gone.
        var sheet = await BuildLevelUpSheetAsync(player);
        if (sheet is { } built)
        {
            await session.SendInternalRmiAsync(ZoneS2COpcodes.OnPcInitTrait, built.InitTrait);
            await session.SendInternalRmiAsync(ZoneS2COpcodes.OnPcTrait, built.Trait);
        }
        else
        {
            ApplyBaseStatMax(player, oldLevel);
        }

        // The level-up refills: the 2394 carries current == max, and that max is the 2498's.
        // Under the vitals lock so the refill is atomic against a concurrent combat write.
        lock (player.Vitals)
        {
            player.Hp = player.MaxHp;
            player.Mp = player.MaxMp;
        }

        // 2394 carries the actor handle at blob+4 (not blob+8) and the exp WITHIN the new level, which is
        // exactly what player.Exp holds after the loop, and the stat points of every level the grant crossed.
        var levelUpBody = CombatPackets.BuildLevelUp2394(
            player.EntityId, player.Level, player.Exp,
            (uint)player.MaxHp, (uint)player.Hp, (uint)player.MaxMp, (uint)player.Mp,
            CombatPackets.StatPointsForLevelUp(oldLevel, player.Level));
        await session.SendRmiAsync(ZoneS2COpcodes.OnLevelUp,
            writer => writer.WriteBytes(levelUpBody));

        // 2395 puts the handle at blob+8 -- the opposite of its sibling 2394. The leveler gets its own copy,
        // and the players on the map theirs, so the map-wide send includes the leveler.
        var notifyBody = CombatPackets.BuildLevelUpNotify2395(player.EntityId, player.Level);
        await _broadcast.BroadcastToMapAsync(player.MapId, ZoneS2COpcodes.OnLevelUpNotify,
            writer => writer.WriteBytes(notifyBody));

        // TalentOnLevelUp — self-notify the client's skill/talent UI with 2542 OnGetTalentPoint. The
        // running total is a pure function of level (one point per level: total == level − 1), so it
        // is RECOMPUTED here rather than kept in a transient tally that would drift or reset on relog.
        // The same value is (re)sent on world entry by WorldEntryService, so a fresh login and a
        // level-up converge on the identical total. One per burst, with the final level.
        var talentTotal = (byte)Math.Clamp(player.Level - 1, 0, 255);

        // 2542 is a COMPACT-BA opcode (msgTag 58381) — never a raw body.
        // Self-notify (unicast), like 2394: the talent gain is addressed to the leveler only.
        await session.SendRmiAsync(ZoneS2COpcodes.OnGetTalentPoint,
            writer => writer.WriteBytes(TalentNotifyPackets.BuildOnGetTalentPoint(player.EntityId, talentTotal)));

        // 2726 NfyGuildBribePCLevelUp goes last in the burst, only to the leveler, and only when the
        // leveler is in a guild; it follows the leveler's own 2542 immediately.
        // One per burst, from the LAST level completed (the newLevel - 1 row; see GuildBribePackets and the
        // remarks of GrantExpAsync). GuildService is loaded at spawn
        // (PlayerSpawnService) and by every guild handler. If it is not loaded, GetByCharId returns
        // null and the notify is skipped, so the player is never told about a guild we cannot read.
        if (GuildService.Instance.GetByCharId(player.CharacterId) is { } guild)
        {
            var bribe = GuildBribePackets.LevelUpBribeAmount(completedLevelExp);
            var bribeBody = GuildBribePackets.BuildNfyGuildBribePCLevelUp(bribe);
            await session.SendRmiAsync(ZoneS2COpcodes.NfyGuildBribePCLevelUp,
                writer => writer.WriteBytes(bribeBody));

            // ...and the guild really earns it: the guild EXP moves by exactly the 2725 + 2726 amounts
            // (see Guild.Exp). The next 2559 of every member shows it (GuildRosterPackets.GuildLevelOf). It is
            // also the member's own contribution: roster +30 moves by the 2725 + 2726 amounts, so the credit goes
            // through CreditMemberExpAsync, not the guild-only AddExpAsync.
            var (before, after) = await GuildService.Instance.CreditMemberExpAsync(guild, player.CharacterId, bribe);
            var oldGuildLevel = GuildRosterPackets.GuildLevelOf(before, _data.GuildExpTable).Level;
            var newGuildLevel = GuildRosterPackets.GuildLevelOf(after, _data.GuildExpTable).Level;
            if (newGuildLevel > oldGuildLevel)
            {
                // 2728 NfyGuildLevelUP [u8 new level] to the online members, right after the credit that
                // crossed the level (within a few milliseconds of the 2725/2726; it can go out to two members'
                // sessions at once).
                var guildLevelUpBody = GuildBribePackets.BuildNfyGuildLevelUp(newGuildLevel);
                await GuildPackets.BroadcastToGuildAsync(guild, _playerManager, _broadcast,
                    ZoneS2COpcodes.NfyGuildLevelUP, guildLevelUpBody);
            }
        }

        // A "Combat level @1 achieved" Khara (Complete_Type 14) the new level reaches completes after the burst: its
        // 2575 / 2576 go out after the 2394 / 2395 / 2542 and, for a guilded leveler, in the same millisecond as the
        // 2726 and after it. Ahead of the 2903 below, which is the last frame of the burst. KharaService reads its
        // stores from a scope it is given.
        if (_scopeFactory is not null)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                await KharaService.OnLevelChangedAsync(session, player, scope.ServiceProvider, _logger);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Khara level check after {Name}'s level-up failed", player.Name);
            }
        }

        // 2903 NfyHelperNotice [helper gid][00] + the 2361 roster with rec+63 cleared, when this level-up put a
        // Helper of the leveler's party outside the client's Helper rule. The 2903 is the LAST frame of the
        // level-up burst, so it goes after the guild block. Every level gain (kill, party share, quest prize,
        // jumping level-up) reaches this method through
        // GrantExpAsync. PartyService is a singleton that does not depend on this service, so taking it from the
        // scope factory adds no cycle and leaves the constructor tests call positionally unchanged; without a
        // scope factory (unit tests) there is no party state to revoke from.
        if (_scopeFactory is not null)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                if (scope.ServiceProvider.GetService<PartyService>() is { } party)
                    await party.RevokeLapsedHelpersAsync(player);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Revoking lapsed Helper flags after {Name}'s level-up failed", player.Name);
            }
        }
    }

    /// <summary>
    /// The 2595/2498 pair of a level-up: the 2498 sheet of <paramref name="player"/> at its new level with its live
    /// HP/SP (EffectService.RefreshPcTraitSheet, which also sets PlayerState.MaxHp/MaxMp and the combat cache to
    /// it), and the 2595 that precedes it. Null when it cannot be built: no scope factory, no character snapshot,
    /// or the worn gear could not be read.
    /// </summary>
    private async Task<(byte[] InitTrait, byte[] Trait)?> BuildLevelUpSheetAsync(PlayerState player)
    {
        if (_scopeFactory is null || player.Character is not { } character) return null;

        // The snapshot keeps the login level (only PlayerState.Level moves here), and the sheet, its stat-point
        // pool and the base HP/SP all read Character.Level.
        AnsMapLoadingHandler.SyncLiveLevel(player, character);

        IReadOnlyList<InventoryItem> equipped;
        EffectService effects;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            effects = scope.ServiceProvider.GetRequiredService<EffectService>();
            equipped = await scope.ServiceProvider.GetRequiredService<IInventoryRepository>()
                .GetEquipmentAsync(player.CharacterId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Name}: could not read the worn equipment; level-up sent without its 2595/2498",
                player.Name);
            return null;
        }

        var trait = effects.RefreshPcTraitSheet(player, character, equipped);
        return (WorldEntryService.BuildPcInitTrait2595(character, trait), trait);
    }

    /// <summary>
    /// The runtime max HP/SP at the new level when no sheet can be built: StatService.ComputeBaseStats over the new
    /// level and the cached gear-adjusted primaries, the formula the 2498 sheet uses, so HP after a level-up matches
    /// HP after a relog. The old flat "+= 50 + Level·5 / += 20 + Level·3" per level survives only for a class with
    /// no PCBaseStatus row (bs.MaxHp == 0). Per-stat fallback to the base primary guards a not-yet-cached edge case.
    /// </summary>
    private void ApplyBaseStatMax(PlayerState player, int oldLevel)
    {
        int C(int cached, int baseVal) => cached > 0 ? cached : baseVal;
        var bs = StatService.ComputeBaseStats(_data, player.Job, player.Level,
            C(player.CombatStr, player.Str), C(player.CombatAgi, player.Agi), C(player.CombatInt, player.Int),
            C(player.CombatWis, player.Wis), C(player.CombatVit, player.Vit), C(player.CombatLuk, player.Luk));
        if (bs.MaxHp > 0)
        {
            player.MaxHp = (int)bs.MaxHp;
            player.MaxMp = bs.MaxSp;
            return;
        }
        // UNVERIFIED: the 50 + level*5 HP and 20 + level*3 SP per-level gains for classless rows are placeholders.
        for (int level = oldLevel + 1; level <= player.Level; level++)
        {
            player.MaxHp += 50 + level * 5;
            player.MaxMp += 20 + level * 3;
        }
    }

    /// <summary>
    /// Heal a player's HP/MP.
    /// </summary>
    public async Task HealAsync(PlayerState player, int hpAmount, int mpAmount)
    {
        // Under the vitals lock, and never on a corpse (TryApply* refuse when IsDead): a heal must not raise the
        // HP of a player a lethal combat/fall/DoT hit has just killed. Each clamps to its maximum as before.
        player.TryApplyHpDelta(hpAmount, out _);
        player.TryApplySpDelta(mpAmount, out _);

        if (_broadcast.GetSession(player.SessionId) == null) return;

        // Reflect the new vitals with the same packet RegenService uses (2401 OnActorAutoHeal), to the player AND
        // the players that see it (ActorVitalsBroadcast). The old send used OnUpdateHP (an alias of
        // 2390 OnAttackFrom — wrong opcode) with a raw body keyed by SessionId, which crashes the client.
        await ActorVitalsBroadcast.SendAutoHealAsync(_broadcast, player);
    }

    /// <summary>
    /// The level a full skill id (group * 100 + level) is allowed to cast at (CB-H1/CB-G2): the id's own level, never
    /// above the level the caster learned for the group (a class skill the caster never learned casts at 0, i.e. no
    /// skill rate) nor above the group's <c>Talent_Table.Max_Skill_Level</c>. The wire level is the client's, so
    /// without this a level-1 skill sent as level 10 dealt level-10 damage. An item, guild or monster skill
    /// (<see cref="SkillDamageHandler.FirstNonClassSkillId"/> and up) is never in <see cref="PlayerState.LearnedSkills"/>,
    /// so only the table cap applies to it (and none when it is not a talent skill).
    /// </summary>
    internal static byte ClampCastLevel(GameDataStore data, PlayerState attacker, uint skillId)
    {
        var group = skillId / 100u;
        var level = (byte)(skillId % 100u);
        if (skillId < SkillDamageHandler.FirstNonClassSkillId)
            level = Math.Min(level, attacker.LearnedSkills.GetValueOrDefault(group));
        if (TalentRules.TryGetSkillLevelLimit(data, group, out var max))
            level = Math.Min(level, max);
        return level;
    }

    private int CalculateBaseDamage(PlayerState attacker, uint skillId)
    {
        // DECODE the incoming skill id. The skill-damage wire (1509 blob+0) carries the FULL id
        // Skill_Group*100 + castLevel (312405 = group 3124 level 5, 310703 = 3107 L3), NOT a
        // bare group. store.Skills is keyed by GROUP, so looking the full id up there missed every time
        // and skill damage silently fell back to the normal-attack formula — which is why a level-1 and a
        // level-10 skill dealt identical damage. Resolve group + cast level from whichever form arrived:
        //   0                -> normal attack (no skill)
        //   a known group    -> that group at the player's learned level
        //   otherwise        -> full id: group = id/100, castLevel = min(id%100, learned level, Max_Skill_Level)
        uint group;
        byte castLevel;
        if (skillId == 0)
        {
            group = 0; castLevel = 0;
        }
        else if (_data.Skills.ContainsKey(skillId))
        {
            group = skillId;
            castLevel = attacker.LearnedSkills.TryGetValue(skillId, out var lg) ? lg : (byte)1;
        }
        else
        {
            group = skillId / 100u;
            castLevel = ClampCastLevel(_data, attacker, skillId);
        }

        // ATTACK POWER — the AoV formula:
        //   Attack = (primary × ClassBonus + ATKBonus) × (1 + TotalATKModifier/100)
        // ClassBonus is the PCBaseStatus coefficient: APValue for physical (× STR), MAValue for magic
        // (× INT). This is exactly what the 2498 sheet shows as Physical / Magic Attack (trait 23 / 25).
        // A class is physical or magic by which coefficient is non-zero (archer AP=2/MA=0 → physical;
        // a mage is the reverse); take the larger so a pure class uses its real stat and a hybrid uses
        // its stronger hand. Gear/buff ATKBonus and the % modifier fold in through the same trait later.
        //
        // GEAR-ADJUSTED primaries: read the cached effective STR/INT (base + allocated + equipment, the
        // same value the 2498 sheet shows), NOT the bare base — otherwise +STR/+INT from gear raised the
        // displayed Attack but never the damage actually dealt. Falls back to the base primary when the
        // effective value has not been resolved yet (0), which never happens once map-load has cached it.
        int str = attacker.CombatStr > 0 ? attacker.CombatStr : attacker.Str;
        int intel = attacker.CombatInt > 0 ? attacker.CombatInt : attacker.Int;
        var pc = _data.PcBaseStatus.GetValueOrDefault(attacker.Job);
        // UNVERIFIED: the str * 2 term for a class with no PcBaseStatus row is a placeholder.
        int physAtk = pc is not null ? pc.APValue * str : str * 2;
        int magAtk  = pc is not null ? pc.MAValue * intel : 0;
        int attack  = Math.Max(1, Math.Max(physAtk, magAtk));

        // SKILL MODIFIER — the per-level DamageRate is the skill's damage multiple (Charge Arrow
        // L1 = 3.3, L2 = 3.9, L3 = 4.5, i.e. the "330/390/450 %" the tooltip shows). A normal attack
        // (no skill) has no rate, so it lands at 1.0 × the base effect.
        double skillMod = 1.0;
        if (group != 0 && castLevel > 0)
        {
            var levelData = _data.GetSkillLevel(group, castLevel);
            if (levelData != null && levelData.DamageRate > 0)
                skillMod = levelData.DamageRate;
        }

        // PHYSICAL/MAGIC SKILL EFFECT (PSE) — the AoV formula:
        //   PSE    = Attack × 0.095 + (WeaponMin + WeaponMax) / 2      (weapon damage dominates)
        //   Damage = (PSE + RefineBonus) × SkillModifier               (RefineBonus = 0, not wired yet)
        // Weapon min/max are TraitInfo trait 96/97, cached on the player where the equipment sheet is
        // recomputed (login, equip swap, socket). With no weapon worn (0/0) the PSE weapon term is 0 and
        // Attack×0.095 alone is tiny, so bare-fisted attacks fall back to Attack × SkillModifier so
        // unarmed damage stays sane.
        int wMin = attacker.WeaponMinDamage;
        int wMax = attacker.WeaponMaxDamage;
        double baseEffect = (wMin > 0 || wMax > 0)
            ? attack * 0.095 + (wMin + wMax) / 2.0   // PSE with the equipped weapon
            : attack;                                // no weapon: keep the raw attack as the base effect

        return Math.Max(1, (int)Math.Round(baseEffect * skillMod));
    }
}
