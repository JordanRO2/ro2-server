using System.Buffers.Binary;
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.GameData;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Models;
using RO2.Inventory.Domain;
using RO2.Characters.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Timed-effect (buff / debuff / DoT / HoT) system. Applies an effect to a target (a player OR a
/// monster), broadcasts 2492 OnAddEffect, ticks damage/heal-over-time effects on a periodic timer,
/// and — on expiry or cancel — removes it with 2493 OnDelEffectSuccess (unicast to a PC owner) followed
/// by 2494 OnDelEffect (broadcast to the map).
/// </summary>
/// <remarks>
/// <para>
/// <b>Message order for a PC.</b> Applying an effect with
/// stat-modifier rows: N × 2499 OnPcTraitChange, then one 2498 OnPcTrait, then the 2492 (see
/// <see cref="ApplyEffectToPlayerAsync"/>). Removing any effect: 2498, then 2493, then 2494, and no 2499,
/// with the PC's 2315 to the map after the 2498 when the removed effect moves the max HP or SP (see
/// <see cref="RemoveEffectAsync"/>). 2498/2499/2493 are unicast to the owner. Both 2498s carry the
/// sheet with the stat rows of the effects then active folded in (see <see cref="SendPcTraitSheetAsync"/>).
/// </para>
/// <para>
/// <b>Two layers, both additive.</b>
/// </para>
/// <list type="number">
/// <item><description><b>Plain timed buff/debuff.</b> An effect with an id, caster, magnitude and
/// expiry is recorded on the target and announced with 2492; on expiry it is removed with 2493/2494.
/// This is the item-use path (<see cref="ApplyItemEffectAsync"/>), unchanged, plus the
/// skill/combat seams <see cref="ApplyEffectToPlayerAsync"/> and <see cref="ApplyEffectToNpcAsync"/>
/// that apply the same to any target.</description></item>
/// <item><description><b>Damage/heal-over-time.</b> An effect may additionally carry a per-tick HP
/// and/or SP delta and a tick interval (see <see cref="ActiveEffect.IsPeriodic"/>). The existing
/// expiry timer then also applies that signed delta to the target's HP/SP <b>through the existing
/// vitals path</b> — <c>NpcStateManager.ApplyDamage</c> for a monster, the <c>PlayerState.Hp/Mp</c>
/// fields for a player — and broadcasts 2495 OnDotDamageNotify (and, for a PLAYER target only,
/// 2496 OnChangeAbnormalSP when the effect carries an SP amount; see <see cref="TickNpcEffectAsync"/> for why a monster
/// gets no 2496) to everyone who can see the target. The per-tick magnitude and interval are either
/// supplied by the caller or, when the caller passes none, read from the effect's own Effect_Table tick
/// rows by <see cref="EffectTickPlan"/> (period row Task 0 / Task_Name 4, Basic_Add_Value in seconds, not
/// the skill's Tick_Interval; HP row Task 1 / Task_Name 3; SP row Task 1 / Task_Name 4; an absolute amount
/// or 1/10,000 of max; see there for details). The one live source is an item effect
/// (<see cref="ApplyItemEffectAsync"/>, reached from 1323 and 1561): the eggs 60001005/60001006 heal and
/// restore SP every 2 s. ⚠️ That is a sliver of the game's 2495 traffic: almost
/// all of it comes from class and NPC skills whose tick rows are sentinels (Basic 0, Level_Multiply ±1)
/// taking the caster's computed effectValue, which no table holds, and <see cref="ApplyDotToNpcAsync"/>
/// still has no caller. For wiring one from the skill-hit path (1509, SkillDamageHandler): the DIRECTION is
/// the row with Task_Name 3 / Trait_Kind 3 / Task_Type 1, whose Level_Multiply_Value is +1 for a heal and −1
/// for damage; each tick is the 2492 value × stack; and that value is in no table: a heal-over-time splits the
/// skill's direct heal across its ticks, while a damage-over-time's 2718 hit carries 0 and each tick a
/// caster-dependent amount.
/// </description></item>
/// </list>
/// <para>
/// <b>Data-driven buff trigger (unchanged).</b> Which effect an item applies, and for how long, come
/// entirely from the item's own master-data columns: <c>ItemInfo.Effect_ID_1</c> (the effect),
/// <c>ItemInfo.Skill_ID_1</c> (the granting skill) and that skill's <c>Effect_Duration</c> seconds
/// times 1000. See <see cref="PlanItemEffect"/>.
/// </para>
/// <para>
/// <b>Recipients follow the rules of the family:</b> 2492/2494/2495/2496 are broadcast to
/// the map with NO exclusion (the actor must receive its own copy or the client never builds the effect
/// instance; a source-excluding helper would wrongly drop it), and 2493 is unicast to a
/// PC owner immediately before the 2494 broadcast. A monster never gets a 2493 — that message is
/// applied to the LOCAL player unconditionally by the client and carries no handle.
/// </para>
/// <para>
/// <b>A DoT can kill a PLAYER.</b> A lethal tick runs the same death sequence as a monster kill, with the 2495
/// in place of the 2696, through <c>NpcCombatService.RunDotDeathAsync</c>: see
/// <see cref="TickPlayerEffectAsync"/>. A DoT on a MONSTER reuses the same loot/death path the skill- and
/// normal-attack kills use.
/// </para>
/// <para>
/// <b>A tick on a dead player changes nothing.</b> A DoT or HoT can still be running on a player whom
/// <c>NpcCombatService</c> has just killed (it sets HP and SP to 0 and <c>IsDead</c>). The 2495 is still sent,
/// but the corpse is neither hurt nor healed. See <see cref="TickPlayerEffectAsync"/>.
/// </para>
/// </remarks>
public sealed class EffectService : BackgroundService
{
    private readonly PlayerStateManager _players;
    private readonly BroadcastService _broadcast;
    private readonly GameDataStore _data;
    private readonly NpcStateManager _npcState;
    private readonly LootService _loot;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IGuildEmblemRepository _emblems;
    private readonly ILogger<EffectService> _logger;

    /// <summary>
    /// Effects on monsters. <c>NpcRuntimeState</c> carries no effect list of its own, so the service
    /// keeps them here, keyed by the NPC's entity handle. A player's effects live on
    /// <c>PlayerState.ActiveEffects</c>. Each list is guarded by locking on the list instance,
    /// matching the player convention.
    /// </summary>
    private readonly ConcurrentDictionary<uint, List<ActiveEffect>> _npcEffects = new();

    /// <summary>
    /// A copy of the effects EffectService holds on the NPC <paramref name="npcHandle"/>, taken under the list's
    /// lock; empty when it has none. Read by ActorViewDataHandler for the effect list of the 2319 record
    /// (NpcSpawnService.BuildOnNpcViewData2319), which is how the 116- and 140-byte 2319s carry them.
    /// </summary>
    internal IReadOnlyList<ActiveEffect> NpcEffectsSnapshot(uint npcHandle)
    {
        if (!_npcEffects.TryGetValue(npcHandle, out var list))
            return Array.Empty<ActiveEffect>();
        lock (list)
            return list.ToArray();
    }

    /// <summary>Expiry / tick resolution. Effect durations run seconds-to-days and DoT intervals are
    /// whole seconds, so a 1 s service tick is ample; sub-second intervals quantise to 1 s.</summary>
    private const int TickMs = 1000;

    /// <summary>
    /// 2492 blob+16. Its meaning is undetermined; it is sent as the constant 1.
    /// </summary>
    internal const ushort UndeterminedEffectValue = 1;

    /// <summary>2492 blob+26: the stack count, sent as 1 (Effect_Table.Stack_Num is 1 for almost all of these items).</summary>
    internal const byte DefaultStackCount = 1;

    /// <summary>Effect level of an effect with no granting skill (and the default of the apply methods):
    /// the stat-modifier magnitude is <c>Basic_Add_Value + Level_Multiply_Value × level</c>, so 1 is the base level.</summary>
    internal const byte DefaultEffectLevel = 1;

    /// <summary>2492 blob source-skill id of an effect that no skill granted (the map-move buff).</summary>
    private const uint NoSourceSkillId = 0;

    /// <summary>2492 applyMode byte (see <see cref="EffectPackets.BuildAddEffect2492"/>): 0 is the usual case,
    /// the only mode this service announces.</summary>
    private const byte AddEffectApplyMode = 0;

    /// <summary>Milliseconds per second, as a float32 for the single-precision duration product in
    /// <see cref="ItemEffectDurationMs"/>.</summary>
    private const float MillisecondsPerSecond = 1000f;

    /// <summary>Lowest HP a damage tick leaves a living player at. A tick never kills on its own: a lethal
    /// one goes through <c>KillByDotAsync</c>.</summary>
    private const int DotHpFloor = 1;

    /// <remarks>
    /// <paramref name="scopeFactory"/> reads the worn equipment for the 2498 stat sheet
    /// (<see cref="SendPcTraitSheetAsync"/>): <c>IInventoryRepository</c> is scoped and this service is a
    /// singleton. <see cref="IServiceScopeFactory"/> is always registered by the host, and Program.cs builds
    /// this service with <c>AddSingleton&lt;EffectService&gt;()</c>, so the container supplies it (the same
    /// pattern as DurabilityService and NpcCombatService).
    /// </remarks>
    public EffectService(PlayerStateManager players, BroadcastService broadcast,
        GameDataStore data, NpcStateManager npcState, LootService loot,
        IServiceScopeFactory scopeFactory, ILogger<EffectService> logger, IGuildEmblemRepository emblems)
    {
        _players = players;
        _broadcast = broadcast;
        _data = data;
        _npcState = npcState;
        _loot = loot;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _emblems = emblems;
    }

    /// <summary>
    /// The (effect, skill, duration, stack, value) an item applies, or <c>null</c> when the item
    /// declares no effect (Effect_ID_1 == 0). Pure and side-effect free so it can be unit-tested
    /// against the real master data.
    /// </summary>
    public readonly record struct EffectPlan(
        uint EffectId, uint SourceSkillId, ulong DurationMs, byte StackCount, ushort EffectValue,
        byte EffectLevel);

    /// <summary>
    /// Derives the effect an item applies from its own columns. Returns <c>null</c> for items with no
    /// Effect_ID_1 (the overwhelming majority, including instant-heal potions), which are left alone.
    /// Pure: <paramref name="grantingSkill"/> is the already-resolved
    /// <c>Skill_Table_SkillLevel</c> row for the item's Skill_ID_1, or <c>null</c> when the item has
    /// no skill or the row is missing — in which case the effect is treated as permanent.
    /// </summary>
    public static EffectPlan? PlanItemEffect(ItemData item, SkillLevelData? grantingSkill)
    {
        if (item.Effect_ID_1 == 0)
            return null;

        ulong durationMs = 0; // 0 = permanent
        if (grantingSkill is { } lvl && lvl.Effect_Duration > 0f)
            durationMs = ItemEffectDurationMs(lvl.Effect_Duration);

        return new EffectPlan(
            EffectId: item.Effect_ID_1,
            SourceSkillId: item.Skill_ID_1,
            DurationMs: durationMs,
            StackCount: DefaultStackCount,
            EffectValue: UndeterminedEffectValue,
            // The stat-modifier magnitude scales with the granting skill's level (Effect_Table's
            // Level_Multiply_Value × level). Read it from the resolved skill row; 1 when the item
            // grants no skill. Never invented — it is the data's own Skill_Level.
            EffectLevel: grantingSkill?.Skill_Level ?? DefaultEffectLevel);
    }

    /// <summary>
    /// The 2492 duration of an item effect: <c>Effect_Duration</c> seconds times 1000, multiplied in SINGLE
    /// precision, as the client expects. Effect_Duration already carries a ~100 ms grace (3600.1).
    /// </summary>
    /// <remarks>
    /// The float32 product matches the wire value. A re-use of a 1-day, 7-day or 30-day item while its effect was
    /// still active adds the remaining time on top (not modelled: a re-use here restarts the
    /// duration). A double product would differ: for a day, 86,400.1f × 1000 is 86,400,101.56, which a float stores
    /// as 86,400,104 and a double rounds to 86,400,102; 7 days go out as 604,800,128 and 3 days as 259,200,096 in
    /// the same way.
    /// </remarks>
    internal static ulong ItemEffectDurationMs(float effectDurationSeconds)
    {
        // The explicit cast narrows to float32 whatever precision the JIT computes the product in.
        float ms = (float)(effectDurationSeconds * MillisecondsPerSecond);
        return ms <= 0f ? 0UL : (ulong)Math.Round((double)ms, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// ItemInfo <c>Item_Type</c> of the siege statues (22270026-22270028, effects 60034-60036). Their effect
    /// is not applied on an item use: see <see cref="ApplyItemEffectAsync"/>.
    /// </summary>
    internal const byte SiegeStatueItemType = 110;

    // =============================================================================================
    // Application (buff / debuff / DoT / HoT)
    // =============================================================================================

    /// <summary>
    /// Applies the effect declared by <paramref name="item"/> to <paramref name="player"/>: records
    /// it for later expiry and sends the application run (N × 2499, 2498, then the 2492 broadcast
    /// to the map, the user included). No-op if the item declares no effect, and for a siege statue.
    /// Called by both item paths: 1323 ReqPCItemUse (ItemUseHandler) and 1561 ReqSkillStart_Item
    /// (SkillStartItemHandler), which is how an item skill's effect is applied.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An item effect with Effect_Table tick rows ticks: <see cref="ApplyEffectToPlayerAsync"/> derives the
    /// period and the HP/SP amounts from them (<see cref="EffectTickPlan"/>). Of the items, only the eggs
    /// 60001005/60001006 (effects 105210/105211, +1% / +2% of max HP and SP every 2 s for 10.1 s) have such
    /// rows that the plan can fill. Their tick is derived from the rows.
    /// </para>
    /// <para>
    /// <b>Siege statues are not applied here.</b> The other three items with tick rows are the statues
    /// (<see cref="SiegeStatueItemType"/>, effects 60034-60036). Their effect is applied after the client's 1509,
    /// not in the item answer: the 2492 comes after the 1509, and it carries effect value 4 or 5, where every other
    /// item effect carries 1 (see SkillStartItemHandler). No column holds that 4 or 5, and the statue's SP gain is
    /// 500 + 200 × that value, so applying it here at 1 would tick 7% where the real value ticks 13-15%. This
    /// server
    /// has no siege war that would place the statue either.
    /// </para>
    /// </remarks>
    public async Task ApplyItemEffectAsync(PlayerState player, ItemData item)
    {
        if (item.Item_Type == SiegeStatueItemType)
        {
            _logger.LogDebug("{Name}: siege statue {Item} used; its effect {Effect} is not applied (see ApplyItemEffectAsync)",
                player.Name, item.ID, item.Effect_ID_1);
            return;
        }

        SkillLevelData? grantingSkill = null;
        if (item.Skill_ID_1 != 0 && _data.SkillLevels.TryGetValue(item.Skill_ID_1, out var lvl))
            grantingSkill = lvl;

        if (PlanItemEffect(item, grantingSkill) is not { } p)
            return;

        await ApplyEffectToPlayerAsync(player, p.EffectId, p.SourceSkillId,
            casterHandle: player.EntityId, casterName: player.Name,
            durationMs: p.DurationMs, effectValue: p.EffectValue, stackCount: p.StackCount,
            effectLevel: p.EffectLevel);

        _logger.LogDebug("Applied item effect {EffectId} (skill {Skill}, {DurationMs} ms) to {Name}",
            p.EffectId, p.SourceSkillId, p.DurationMs, player.Name);
    }

    /// <summary>
    /// Applies an effect to a PLAYER and announces it with 2492. Set <paramref name="tickIntervalMs"/>
    /// and a signed <paramref name="hpPerTick"/> / <paramref name="spPerTick"/> to make it a DoT/HoT
    /// (the tick loop then applies the delta and broadcasts 2495/2496). Left all 0, the effect ticks
    /// only if its own Effect_Table rows say so (<see cref="EffectTickPlan"/>, from
    /// <paramref name="effectValue"/>, <paramref name="stackCount"/> and the player's max HP/SP now);
    /// otherwise it is a plain buff. This is the seam every player effect goes through.
    /// </summary>
    /// <param name="hpPerTick">Signed HP change per tick; NEGATIVE damages, POSITIVE heals.</param>
    /// <param name="spPerTick">Signed SP change per tick; drives 2496 OnChangeAbnormalSP.</param>
    /// <param name="tickIntervalMs">Tick period; 0 = not periodic (a plain buff).</param>
    public async Task ApplyEffectToPlayerAsync(PlayerState player, uint effectId, uint sourceSkillId,
        uint casterHandle, string? casterName, ulong durationMs, ushort effectValue = UndeterminedEffectValue,
        byte stackCount = DefaultStackCount, int hpPerTick = 0, int spPerTick = 0, int tickIntervalMs = 0,
        byte effectLevel = DefaultEffectLevel)
    {
        // No caller-supplied tick: the effect's own rows decide. The max HP/SP are the ones in force before
        // this application's 2498 (see EffectTickPlan on when the max is read).
        if (hpPerTick == 0 && spPerTick == 0 && tickIntervalMs == 0
            && EffectTickPlan.For(_data.EffectTable, effectId, effectValue, stackCount, player.MaxHp, player.MaxMp)
                is { } ticks)
        {
            (hpPerTick, spPerTick, tickIntervalMs) = (ticks.HpPerTick, ticks.SpPerTick, ticks.IntervalMs);
        }

        var now = DateTime.UtcNow;
        var effect = new ActiveEffect
        {
            EffectId = effectId,
            SourceSkillId = sourceSkillId,
            EffectValue = effectValue,
            StackCount = stackCount,
            EffectLevel = effectLevel,
            CasterHandle = casterHandle,
            CasterName = casterName,
            DurationMs = durationMs,
            ExpiresAt = durationMs == 0 ? (DateTime?)null : now.AddMilliseconds(durationMs),
            TargetHandle = player.EntityId,
            TargetIsNpc = false,
            MapId = player.MapId,
            HpPerTick = hpPerTick,
            SpPerTick = spPerTick,
            TickIntervalMs = tickIntervalMs,
            NextTickAt = tickIntervalMs > 0 ? now.AddMilliseconds(tickIntervalMs) : default,
        };

        lock (player.ActiveEffects)
        {
            // Re-applying the same effect refreshes rather than stacks a second copy of the same id.
            player.ActiveEffects.RemoveAll(e => e.EffectId == effectId);
            player.ActiveEffects.Add(effect);
        }

        // An effect with stat-modifier rows is announced as N x 2499 -> one 2498 -> the 2492; the owner's own 2492
        // does not come first. A 2497 or 2559 may sit in between. This used to send the 2492 first and no 2498 at
        // all. Effects with no modifier rows get no 2498.
        if (await SendPlayerTraitModifiersAsync(player, effect)
            && _broadcast.GetSession(player.SessionId) is { } session)
        {
            await SendPcTraitSheetAsync(player, session);
        }

        await AnnounceAddEffectAsync(player.MapId, player.EntityId, effect);
    }

    /// <summary>
    /// The map-move buff run of the reply to a 1304 AnsMapLoading, in the slot where that reply's 2498 goes: the
    /// buff (<see cref="MapMoveBuff"/>: Config_Def 65/66, Effect_Table 60205 for 10 s) is recorded in
    /// <c>ActiveEffects</c>, its 2499 lines go to <paramref name="session"/>, then ONE 2498, the map-load sheet
    /// (<see cref="RefreshMapLoadPcTraitSheet"/>, which now folds the buff in), then the 2492 to the map. Returns after
    /// sending; the expiry is the tick loop's (2498 -> 2493 -> 2494, <see cref="RemoveEffectAsync"/>), and a 1463 cancel
    /// (<see cref="CancelPlayerEffectAsync"/>, Picking_Delete_Type 1) or a warp departure (WarpDeparture, Warp_Handling 0)
    /// ends it earlier. When Config_Def lacks the rows, only the sheet goes out, as before.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE RUN. Every map-load reply that carries the buff sends
    /// 2497 -> [2986] -> 2499 (kind 4, trait 51, -10000, 60205) -> 2499 (kind 4, trait 35, +10000, 60205) -> 2498 ->
    /// 2492 (60205) -> 2811, and the 2498 there differs from the removal one 10.4 s later only in the u16 at blob+64
    /// (10000 -> 0), which is the trait-35 line (<see cref="FoldSheetModifiers"/>). So the reply sends no second
    /// 2498: the buff's run takes the slot of the sheet the reply already sent. The 2499 lines and the 2492 are the
    /// generic ones (<see cref="BuildTraitChangeLines"/>, <see cref="AnnounceAddEffectAsync"/>), with value 1,
    /// duration 10000, stack 1, skill 0, mode 0, caster the PC itself: <see cref="UndeterminedEffectValue"/>,
    /// Config_Def 66 × 1000, <see cref="DefaultStackCount"/>, no source skill, the PC's own handle.
    /// </para>
    /// <para>
    /// WHAT IT GRANTS. Miss_Rate +10000 is +100%: <see cref="MissRateOf"/> reads it, and NpcCombatService turns every
    /// monster hit on a PC at that rate into a kind-2 miss (no damage, no armour wear), so the client's
    /// "invincible" icon is what the server does. It ends early on a 1463 cancel and on a warp departure
    /// (1615/1528/1371), both handled; it also ends on some 1508 skill starts, which is not a deterministic rule and
    /// is not modelled: here the buff then runs its 10 s. A normal attack (1498/1499) never ends it.
    /// </para>
    /// <para>
    /// Not every map load carries it; the replies without it mostly also leave out the 2506/2315/2497 block, which
    /// this server always sends, so the buff goes with it on every 1304.
    /// </para>
    /// </remarks>
    internal async Task ApplyMapMoveBuffAsync(ProudSession session, PlayerState player, Character character,
        IReadOnlyList<InventoryItem> equipped)
    {
        if (MapMoveBuff.Of(_data.ConfigDef) is not { } buff)
        {
            await session.SendInternalRmiAsync(ZoneS2COpcodes.OnPcTrait, RefreshMapLoadPcTraitSheet(player, character, equipped));
            return;
        }

        var now = DateTime.UtcNow;
        var effect = new ActiveEffect
        {
            EffectId = buff.EffectId,
            SourceSkillId = NoSourceSkillId,
            EffectValue = UndeterminedEffectValue,
            StackCount = DefaultStackCount,
            EffectLevel = DefaultEffectLevel,
            CasterHandle = player.EntityId,
            CasterName = player.Name,
            DurationMs = buff.DurationMs,
            ExpiresAt = now.AddMilliseconds(buff.DurationMs),
            TargetHandle = player.EntityId,
            TargetIsNpc = false,
            MapId = player.MapId,
        };
        lock (player.ActiveEffects)
        {
            // A second map load inside the 10 s restarts the buff rather than stacking a second copy.
            player.ActiveEffects.RemoveAll(e => e.EffectId == buff.EffectId);
            player.ActiveEffects.Add(effect);
        }

        foreach (var line in BuildTraitChangeLines(effect.EffectId, effect.EffectLevel))
            await session.SendInternalRmiAsync(ZoneS2COpcodes.OnPcTraitChange, line);
        await session.SendInternalRmiAsync(ZoneS2COpcodes.OnPcTrait, RefreshMapLoadPcTraitSheet(player, character, equipped));
        await AnnounceAddEffectAsync(player.MapId, player.EntityId, effect);
    }

    /// <summary>
    /// Applies an effect to a MONSTER and announces it with 2492. Same knobs as the player overload;
    /// a negative <paramref name="hpPerTick"/> with a non-zero <paramref name="tickIntervalMs"/> is a
    /// DoT that ticks the monster's HP down through <c>NpcStateManager.ApplyDamage</c>. No-op for an
    /// unknown or dead NPC. This is the skill/combat seam for monster-targeted effects.
    /// </summary>
    public Task ApplyEffectToNpcAsync(NpcRuntimeState npc, uint effectId, uint sourceSkillId,
        uint casterHandle, string? casterName, ulong durationMs, ushort effectValue = UndeterminedEffectValue,
        byte stackCount = DefaultStackCount, int hpPerTick = 0, int spPerTick = 0, int tickIntervalMs = 0,
        byte effectLevel = DefaultEffectLevel)
    {
        if (npc.IsDead)
            return Task.CompletedTask;

        var now = DateTime.UtcNow;
        var effect = new ActiveEffect
        {
            EffectId = effectId,
            SourceSkillId = sourceSkillId,
            EffectValue = effectValue,
            StackCount = stackCount,
            EffectLevel = effectLevel,
            CasterHandle = casterHandle,
            CasterName = casterName,
            DurationMs = durationMs,
            ExpiresAt = durationMs == 0 ? (DateTime?)null : now.AddMilliseconds(durationMs),
            TargetHandle = npc.EntityOid,
            TargetIsNpc = true,
            MapId = npc.MapId,
            HpPerTick = hpPerTick,
            SpPerTick = spPerTick,
            TickIntervalMs = tickIntervalMs,
            NextTickAt = tickIntervalMs > 0 ? now.AddMilliseconds(tickIntervalMs) : default,
        };

        var list = _npcEffects.GetOrAdd(npc.EntityOid, _ => new List<ActiveEffect>());
        lock (list)
        {
            list.RemoveAll(e => e.EffectId == effectId);
            list.Add(effect);
        }

        return AnnounceAddEffectAsync(npc.MapId, npc.EntityOid, effect);
    }

    /// <summary>
    /// Convenience for the combat/skill system: apply a damage-over-time effect to a monster that
    /// ticks <paramref name="damagePerTick"/> HP off it every <paramref name="tickIntervalMs"/> ms
    /// for <paramref name="durationMs"/> ms. <paramref name="damagePerTick"/> is a POSITIVE amount of
    /// damage (stored as a negative HP delta). The caller computes it — typically from the same
    /// skill-damage math the direct hit uses.
    /// </summary>
    public Task ApplyDotToNpcAsync(NpcRuntimeState npc, uint effectId, uint sourceSkillId,
        uint casterHandle, string? casterName, int damagePerTick, int tickIntervalMs, ulong durationMs)
        => ApplyEffectToNpcAsync(npc, effectId, sourceSkillId, casterHandle, casterName, durationMs,
            hpPerTick: -Math.Max(0, damagePerTick), tickIntervalMs: tickIntervalMs);

    /// <summary>Broadcasts 2492 OnAddEffect for one applied effect to the target's map (no exclusion,
    /// so the target receives its own copy). Shared by every application path.</summary>
    private Task AnnounceAddEffectAsync(uint mapId, uint targetHandle, ActiveEffect effect)
    {
        var body = EffectPackets.BuildAddEffect2492(
            targetHandle: targetHandle,
            effectId: effect.EffectId,
            effectValue: effect.EffectValue,
            durationMs: effect.DurationMs,
            stackCount: effect.StackCount,
            sourceSkillId: effect.SourceSkillId,
            casterHandle: effect.CasterHandle,
            applyMode: AddEffectApplyMode);

        return _broadcast.BroadcastToMapAsync(mapId, ZoneS2COpcodes.OnAddEffect,
            w => w.WriteBytes(body));
    }

    // =============================================================================================
    // Stat-modifier emission (2499 OnPcTraitChange)
    // =============================================================================================

    /// <summary>
    /// One Effect_Table stat-modifier row, sent as one 2499 line whose value is computed from the
    /// effect's level. The client keys the line by (<see cref="Kind"/>, <see cref="TraitId"/>, effect id) in a
    /// 25-byte modifier vector and hands the vector to the actor's skill object. It does NOT add the line to the
    /// character sheet: the sheet is the 2498 block, which is why every run is closed with a 2498. Trait_Kind
    /// selects the modifier bucket (3 and 4 are the two stat-modifier kinds), Task_Type is the trait id (0–5 the
    /// primaries, 23 PhysAtk, 25 MagAtk, …).
    /// </summary>
    private readonly record struct StatModifier(uint Kind, uint TraitId, float BasicAdd, float LevelMul);

    /// <summary>Effect_Table <c>Level_Multiply_Value</c> that marks a caller-supplied magnitude; see
    /// <see cref="ModifierIndex"/>.</summary>
    private const float ExternalMagnitudeSentinel = -1f;

    /// <summary>Effect_Table <c>Task_Name</c> of the rows that carry a stat modifier sent as a 2499 line
    /// (together with <see cref="ModifierTaskNameAlt"/>); Trait_Kind 3/4 rows with any other Task_Name are not sent.</summary>
    private const byte ModifierTaskName = 2;

    /// <summary>The second Effect_Table <c>Task_Name</c> whose Trait_Kind 3/4 rows are sent as 2499 lines.</summary>
    private const byte ModifierTaskNameAlt = 12;

    /// <summary>
    /// effectId → stat-modifier rows, built once from Effect_Table. An effect with no such row (a pure
    /// DoT/visual) maps to an empty list and emits no 2499. Lazily initialised and immutable thereafter.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A row is a 2499 line only when <c>Trait_Kind ∈ {3,4}</c> AND <c>Task_Name ∈ {2,12}</c>. The Task_Name test is
    /// needed: Trait_Kind 3/4 rows with any other Task_Name are never sent, including the "self-reference" rows
    /// (Trait_Kind 3, Task_Type 0, Basic_Add = the effect's own id, Task_Name 1), which a kind-only filter turned
    /// into a trait-0 modifier worth e.g. 60205 or 4108.
    /// </para>
    /// <para>
    /// ⚠️ Rows with <c>Level_Multiply_Value == -1</c> are skipped. −1 is a sentinel, not a multiplier:
    /// 134 table rows carry it (Basic_Add 0 in 133), no real multiplier is smaller in magnitude than
    /// −100, and the magnitude actually sent for them is caller-supplied (e.g. −2000, −300, −5000), never
    /// <c>Basic + (−1)·level</c>. That magnitude is not in our skill tables either, so the line cannot be filled
    /// and is left out rather than sent with a made-up value. For ordinary rows the formula <c>Basic + Mul ×
    /// grantingSkillLevel</c> holds.
    /// </para>
    /// </remarks>
    private Dictionary<uint, List<StatModifier>>? _modifierIndex;
    private readonly Lock _modifierIndexLock = new();

    private Dictionary<uint, List<StatModifier>> ModifierIndex
    {
        get
        {
            if (_modifierIndex != null)
                return _modifierIndex;
            lock (_modifierIndexLock)
            {
                if (_modifierIndex != null)
                    return _modifierIndex;
                var idx = new Dictionary<uint, List<StatModifier>>();
                foreach (var row in _data.EffectTable)
                {
                    if (row.Trait_Kind is not ((byte)FlatModifierKind or (byte)PercentModifierKind))
                        continue;
                    if (row.Task_Name is not (ModifierTaskName or ModifierTaskNameAlt))
                        continue;
                    if (row.Level_Multiply_Value == ExternalMagnitudeSentinel)
                        continue;
                    if (!idx.TryGetValue(row.ID, out var list))
                        idx[row.ID] = list = new List<StatModifier>();
                    list.Add(new StatModifier(row.Trait_Kind, row.Task_Type,
                        row.Basic_Add_Value, row.Level_Multiply_Value));
                }
                return _modifierIndex = idx;
            }
        }
    }

    /// <summary>
    /// Emits one 2499 OnPcTraitChange per stat-modifier row the effect carries, unicast to the owning
    /// PC, with the magnitude <c>Basic_Add_Value + Level_Multiply_Value × EffectLevel</c> (data-derived,
    /// never invented). Returns <c>true</c> when at least one line went out, which is when the caller
    /// must close the run with a 2498 (see <see cref="ApplyEffectToPlayerAsync"/>). No-op, returning
    /// <c>false</c>, for effects with no modifier rows or when the owner has no live session.
    /// </summary>
    /// <remarks>
    /// Sent on APPLICATION only. Removal sends no 2499: the client's 2494 handler itself erases every 25-byte line
    /// whose effect id equals the removed effect from its modifier vector. See <see cref="RemoveEffectAsync"/>.
    /// </remarks>
    private async Task<bool> SendPlayerTraitModifiersAsync(PlayerState player, ActiveEffect effect)
    {
        var lines = BuildTraitChangeLines(effect.EffectId, effect.EffectLevel);
        if (lines.Count == 0)
            return false;

        var session = _broadcast.GetSession(player.SessionId);
        if (session == null)
            return false;

        foreach (var line in lines)
            await session.SendInternalRmiAsync(ZoneS2COpcodes.OnPcTraitChange, line);
        return true;
    }

    /// <summary>
    /// The 2499 bodies an application of <paramref name="effectId"/> at <paramref name="effectLevel"/>
    /// sends, in the order they go out: one per stat-modifier row of <see cref="ModifierIndex"/>, in
    /// Effect_Table row order, valued <c>Basic_Add_Value + Level_Multiply_Value × effectLevel</c>. Empty
    /// for an effect with no such row.
    /// </summary>
    /// <remarks>
    /// Row order is the table order: consecutive 2499s of one effect list exactly the effect's rows, in table
    /// order (for the ranking buff 50603, whose rows are traits 0, 1, 4, 2, 3, that is the order on the wire).
    /// </remarks>
    internal IReadOnlyList<byte[]> BuildTraitChangeLines(uint effectId, byte effectLevel)
    {
        if (!ModifierIndex.TryGetValue(effectId, out var mods) || mods.Count == 0)
            return Array.Empty<byte[]>();

        var lines = new byte[mods.Count][];
        for (int i = 0; i < mods.Count; i++)
        {
            var m = mods[i];
            float value = m.BasicAdd + m.LevelMul * effectLevel;
            lines[i] = TraitPackets.BuildPcTraitChange2499(m.Kind, m.TraitId, value, effectId);
        }
        return lines;
    }

    /// <summary>
    /// Sends the owner the 2498 OnPcTrait stat sheet WITH its active effects folded in
    /// (<see cref="BuildPcTraitSheet"/>): the character, the worn equipment, the LIVE
    /// <c>PlayerState.Hp/Mp</c>, and the Trait_Kind 3/4 rows of every effect in <c>ActiveEffects</c>, through
    /// <see cref="RefreshPcTraitSheet"/>, which also sets <c>PlayerState.MaxHp/MaxMp</c> to the sheet's max HP/SP,
    /// clamps Hp/Mp into them and puts the same buffed stats in the combat cache. Skipped,
    /// sending nothing, when the character snapshot is missing or the equipment cannot be read — a sheet
    /// built without the gear would repaint every gear bonus away.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHY THE FOLD. The client's character sheet is the 2498 block and nothing else: the 2498 handler copies the
    /// blob into the character status and the sheet UI reads it there, while the 2499 lines go to a separate
    /// modifier vector handed to the actor's skill object. So a buff reaches the sheet only through the 2498.
    /// The 2498 closing a 2499 run moves by the run's rows (e.g. effect 40011, Trait_Kind 4 / trait 21 / 1000.0:
    /// max HP +10%), and the 2498 before a 2493 restates the sheet without the removed effect but with the others
    /// that are still active.
    /// </para>
    /// <para>
    /// WHY THE SERVER MAX FOLLOWS IT. Every 2401 this server sends (RegenService, each second while HP or
    /// SP is below its max) carries <c>PlayerState.MaxHp/MaxMp</c>, and the client sets its max HP from
    /// 2401 as well. With the un-buffed server max, a +10% max HP buff would last on the bar
    /// until the next regen tick, and regen would stop at the un-buffed max. Every other in-world 2498 sender
    /// goes through the same <see cref="RefreshPcTraitSheet"/>. Current HP is NOT raised with the max (a buff does
    /// not heal). When the max drops (a removal), current HP is cut to it.
    /// </para>
    /// <para>
    /// Simultaneous expiries: the expiry loop takes every due effect out of <c>ActiveEffects</c> under one
    /// lock, so the first of their removal sheets already lacks all of them. The loop takes them together
    /// to keep a concurrent 1463 cancel from removing the same effect a second time.
    /// </para>
    /// </remarks>
    /// <returns>The worn gear the sheet was built from, or <c>null</c> when no 2498 was sent. A removal re-dresses
    /// the PC with the same list (see <see cref="RemoveEffectAsync"/>), so its 2315 and this 2498 agree.</returns>
    private async Task<IReadOnlyList<InventoryItem>?> SendPcTraitSheetAsync(PlayerState player, ProudSession session)
    {
        if (player.Character is not { } character)
            return null;

        IReadOnlyList<InventoryItem> equipped;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var inventory = scope.ServiceProvider.GetRequiredService<IInventoryRepository>();
            equipped = await inventory.GetEquipmentAsync(player.CharacterId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Name}: could not read the worn equipment; no 2498 with this effect change",
                player.Name);
            return null;
        }

        await session.SendInternalRmiAsync(ZoneS2COpcodes.OnPcTrait, RefreshPcTraitSheet(player, character, equipped));
        return equipped;
    }

    // =============================================================================================
    // The in-world 2498: the one builder every sender goes through
    // =============================================================================================

    /// <summary>
    /// The 2498 OnPcTrait of <paramref name="player"/> in world: the worn
    /// <paramref name="equipped"/> gear, the live HP/SP and the sheet rows of every effect in
    /// <c>PlayerState.ActiveEffects</c> (<see cref="BuildPcTraitSheet"/>; with no sheet row active it is byte for
    /// byte <see cref="WorldEntryService.BuildFullPcTrait2498"/>). Changes nothing on the player: for a sheet
    /// shown to someone else (the 3033 review) or sent ahead of a map change whose 1304 settles the server state.
    /// A PC's own re-push goes through <see cref="RefreshPcTraitSheet"/>.
    /// </summary>
    internal byte[] BuildInWorldPcTrait(PlayerState player, Character character, IReadOnlyList<InventoryItem> equipped)
        => BuildSheet(character, equipped, player.Hp, player.Mp, SumSheetModifiers(ActiveEffectsOf(player)));

    /// <summary>
    /// The 2498 a PC's own session gets after anything that changes its sheet (an effect, gear, refine, socket,
    /// repair, stat points, a class change), with the server state set to it: <c>PlayerState.MaxHp/MaxMp</c>
    /// become the sheet's max HP/SP, Hp/Mp are cut to them, and the combat cache
    /// (<c>PlayerState.CacheCombatStats</c>) carries the same buffed primaries, defense, crit, parry, vigor and
    /// penetration (<see cref="WithSheetDelta"/>). Returns the body; the caller sends it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHY ONE PATH. The client's character sheet is the 2498 and nothing else (see
    /// <see cref="SendPcTraitSheetAsync"/>), and the buffed sheet is restated in every one: the 2498s sent while an
    /// effect such as 40011 (+10% max HP) is active carry the buffed max HP, not the unbuffed one. Built without
    /// the effects, a gear swap, refine, socket, repair, stat point, class change or map load would send the
    /// unbuffed sheet and cut the server max and HP to it while the effect stayed active.
    /// </para>
    /// <para>
    /// WHY THE SERVER MAX FOLLOWS IT. Every 2401 carries <c>PlayerState.MaxHp/MaxMp</c> and the client sets its
    /// max from 2401 too, so a server max under the sheet's snaps the bar back on the next hit or regen tick, and
    /// regen stops at it. Current HP is not raised with the max (a buff does not heal) but is cut when the max
    /// drops.
    /// </para>
    /// <para>
    /// WHY THE COMBAT CACHE FOLLOWS IT. Combat reads the cached primaries and derived stats, not the sheet. Left
    /// at StatService.EffectivePlayerStats they are the gear-only values, so a buff the sheet shows (a +10% STR
    /// row) would not reach a hit. The attack rows (23/25, e.g. 40012's +10%) have no slot in the cache, since
    /// the combat formula derives damage from STR/INT and the weapon, so they still show only on the sheet.
    /// </para>
    /// </remarks>
    internal byte[] RefreshPcTraitSheet(PlayerState player, Character character, IReadOnlyList<InventoryItem> equipped)
        => RefreshSheet(player, character, equipped, refillIfFull: false);

    /// <summary>
    /// <see cref="RefreshPcTraitSheet"/> for the 2498 of the reply to a 1304 map load: the same sheet, server max
    /// and combat cache, except that a PC that was at full HP (or SP) before stays at full, the new max.
    /// </summary>
    /// <remarks>
    /// "Was at full" is read from the LIVE PlayerState before its max is overwritten, not from the world-entry
    /// <c>PlayerState.Character</c> snapshot. That snapshot keeps its login Hp/Mp for the whole session, so a test
    /// against it said "full" on every map load of a PC that logged in at full, and each portal refilled it
    /// whatever its HP. At the first map load after login the two agree: LoginPCHandler seeds PlayerState.Hp/MaxHp
    /// from that same row with the naked max. After it, PlayerState.MaxHp is the sheet max the last refresh set
    /// (gear + active effects), so the live test compares like with like. A buff whose max HP row did not heal
    /// leaves the PC below that max, so it is not refilled here either. The max does not depend on the current
    /// HP/SP the sheet is built with, so the first build gives the max and a second carries the refilled values.
    /// </remarks>
    internal byte[] RefreshMapLoadPcTraitSheet(PlayerState player, Character character, IReadOnlyList<InventoryItem> equipped)
        => RefreshSheet(player, character, equipped, refillIfFull: true);

    private byte[] RefreshSheet(PlayerState player, Character character, IReadOnlyList<InventoryItem> equipped,
        bool refillIfFull)
    {
        var modifiers = SumSheetModifiers(ActiveEffectsOf(player));
        bool wasFullHp = player.Hp >= player.MaxHp;
        bool wasFullSp = player.Mp >= player.MaxMp;

        var body = BuildSheet(character, equipped, player.Hp, player.Mp, modifiers);
        var blob = body.AsSpan(TraitPackets.PcTraitBodyLen - TraitPackets.PcTraitBlobLen);
        int maxHp = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(blob[TraitPackets.PcTraitMaxHpOffset..]), int.MaxValue);
        int maxSp = BinaryPrimitives.ReadUInt16LittleEndian(blob[TraitPackets.PcTraitMaxSpOffset..]);

        int hp = refillIfFull && wasFullHp ? maxHp : Math.Min(player.Hp, maxHp);
        int sp = refillIfFull && wasFullSp ? maxSp : Math.Min(player.Mp, maxSp);
        bool raised = hp > player.Hp || sp > player.Mp;
        // The maxima and the resolved current values are set together under the vitals lock, so the sheet's
        // current/max pair is atomic against a concurrent combat write (and a damage path never sees a torn state).
        lock (player.Vitals)
        {
            player.MaxHp = maxHp;
            player.MaxMp = maxSp;
            player.Hp = hp;
            player.Mp = sp;
        }
        // The builder already cuts the current values to the max; only a refill changes what the sheet carries.
        if (raised)
            body = BuildSheet(character, equipped, hp, sp, modifiers);

        var combat = StatService.EffectivePlayerStats(_data, character, equipped);
        if (!modifiers.IsEmpty)
            combat = WithSheetDelta(combat, WorldEntryService.BuildFullPcTrait2498(character, _data, equipped, hp, sp), body);
        player.CacheCombatStats(combat);
        return body;
    }

    /// <summary>The sheet with <paramref name="modifiers"/> folded in, the plain one when there are none.</summary>
    private byte[] BuildSheet(Character character, IReadOnlyList<InventoryItem> equipped, int hp, int sp,
        EffectSheetModifiers modifiers)
        => modifiers.IsEmpty
            ? WorldEntryService.BuildFullPcTrait2498(character, _data, equipped, hp, sp)
            : BuildPcTraitSheet(character, _data, StatService.ComputeEquipmentStats(_data, equipped), hp, sp, modifiers);

    private static ActiveEffect[] ActiveEffectsOf(PlayerState player)
    {
        lock (player.ActiveEffects)
            return player.ActiveEffects.ToArray();
    }

    /// <summary>Trait ids the combat cache keeps beside the primaries: 26 defense, 60 crit, 61 parry, 62 vigor,
    /// 94 penetration (see EffectiveStats).</summary>
    private const byte DefenseTraitId = 26, CritTraitId = 60, ParryTraitId = 61, VigorTraitId = 62, PenTraitId = 94;

    /// <summary>Byte width of one primary stat (STR, AGI, INT, WIS, VIT, LUK) in the 2498 blob: a u16, laid out
    /// consecutively from <see cref="TraitPackets.PcTraitPrimaryStatsOffset"/>.</summary>
    private const int PrimaryStatWidth = sizeof(ushort);

    /// <summary>
    /// <paramref name="combat"/> (the gear-only stats) moved by what the active effects add to the sheet: each
    /// cached stat gains <c>buffed - plain</c> at its 2498 slot (primaries at blob+14..+24, defense/crit/parry/
    /// vigor/penetration at their <see cref="TraitPackets.DerivedStatBlobOffsets"/>), and max HP/SP are the
    /// buffed sheet's. Both bodies are 143-byte 2498s over the same gear; with no effect row they are equal and
    /// <paramref name="combat"/> comes back unchanged.
    /// </summary>
    internal static EffectiveStats WithSheetDelta(EffectiveStats combat,
        ReadOnlySpan<byte> plainBody, ReadOnlySpan<byte> buffedBody)
    {
        var plain = plainBody[(TraitPackets.PcTraitBodyLen - TraitPackets.PcTraitBlobLen)..];
        var buffed = buffedBody[(TraitPackets.PcTraitBodyLen - TraitPackets.PcTraitBlobLen)..];
        static int P(ReadOnlySpan<byte> p, ReadOnlySpan<byte> b, int i)
            => Delta(p, b, TraitPackets.PcTraitPrimaryStatsOffset + PrimaryStatWidth * i);
        static int D(ReadOnlySpan<byte> p, ReadOnlySpan<byte> b, byte traitId)
            => Delta(p, b, TraitPackets.DerivedStatBlobOffsets[traitId]);

        return combat with
        {
            Str = combat.Str + P(plain, buffed, 0),
            Agi = combat.Agi + P(plain, buffed, 1),
            Int = combat.Int + P(plain, buffed, 2),
            Wis = combat.Wis + P(plain, buffed, 3),
            Vit = combat.Vit + P(plain, buffed, 4),
            Luk = combat.Luk + P(plain, buffed, 5),
            MaxHp = BinaryPrimitives.ReadUInt32LittleEndian(buffed[TraitPackets.PcTraitMaxHpOffset..]),
            MaxSp = BinaryPrimitives.ReadUInt16LittleEndian(buffed[TraitPackets.PcTraitMaxSpOffset..]),
            Defense = combat.Defense + D(plain, buffed, DefenseTraitId),
            Crit = combat.Crit + D(plain, buffed, CritTraitId),
            Parry = combat.Parry + D(plain, buffed, ParryTraitId),
            Vigor = combat.Vigor + D(plain, buffed, VigorTraitId),
            Pen = combat.Pen + D(plain, buffed, PenTraitId),
        };
    }

    private static int Delta(ReadOnlySpan<byte> plain, ReadOnlySpan<byte> buffed, int offset)
        => BinaryPrimitives.ReadUInt16LittleEndian(buffed[offset..]) - BinaryPrimitives.ReadUInt16LittleEndian(plain[offset..]);

    // =============================================================================================
    // Active effects folded into the 2498 stat sheet
    // =============================================================================================

    /// <summary>Effect_Table <c>Trait_Kind</c> of a FLAT stat-modifier row: the value is an amount added to
    /// the stat (e.g. effect 60037's (3, 21, 1000.0) adds 1,000 max HP when no percent row on max HP is active,
    /// and 104001's (3, 21, 3000.0) adds 3,750 when +25% is; see <see cref="FoldSheetModifiers"/>).</summary>
    internal const uint FlatModifierKind = 3;

    /// <summary>Effect_Table <c>Trait_Kind</c> of a PERCENT stat-modifier row, in 1/10,000: 1000.0 is +10%
    /// (e.g. 40011's (4, 21, 1000.0) on max HP).</summary>
    internal const uint PercentModifierKind = 4;

    /// <summary>The unit of a Trait_Kind 4 value: 10,000 = +100%. See <see cref="PercentModifierKind"/>.</summary>
    private const double PercentModifierScale = 10000.0;

    /// <summary>Trait ids of max HP and max SP (Trait_Character_Info 21 Max_HP, 22 Max_SP), carried by the
    /// 2498 at blob+8 (u32) and blob+12 (u16).</summary>
    private const byte MaxHpTraitId = 21;
    private const byte MaxSpTraitId = 22;

    /// <summary>Trait ids 0..5 are the six primaries (TraitInfo Trait_Type 0..5 = STR/AGI/INT/WIS/VIT/LUK),
    /// the 2498's blob+14..+24.</summary>
    private const byte LastPrimaryTraitId = 5;

    /// <summary>Trait id of the evasion rate: TraitBasicInfo TraitID 35 <c>Miss_Rate</c> ('회피율'). Its Trait_Kind 4
    /// rows are in 1/10,000; the map-move buff 60205 carries +10,000. See <see cref="MissRateBlobOffset"/>.</summary>
    internal const byte MissRateTraitId = 35;

    /// <summary>
    /// The 2498 slot of <see cref="MissRateTraitId"/>: the u16 at blob+64, which takes the Trait_Kind 4 trait-35 lines
    /// RAW, as a sum, not as a percent of a base (the base is 0).
    /// </summary>
    /// <remarks>
    /// Tracking each session's 2499 lines (dropped on the 2493 of their effect), blob+64 equals the sum of the
    /// tracked kind-4 trait-35 values; WorldEntryService has the warp-burst copy of the same. Raw and
    /// additive: two +10,000 lines give 20,000, a pet's 1,200 over a 10,000 gives 11,200. Trait_Kind 3 trait-35
    /// lines are NOT folded: no single rule fits them, so they are left out rather than guessed.
    /// </remarks>
    internal const int MissRateBlobOffset = 64;

    /// <summary>
    /// The summed stat-sheet rows of a PC's active effects, keyed by trait id, in Effect_Table units:
    /// <c>Flat</c> the Trait_Kind 3 amounts, <c>Percent</c> the Trait_Kind 4 values in
    /// 1/10,000. Only traits the 2498 carries are kept (see <see cref="IsSheetTrait"/>).
    /// </summary>
    internal readonly record struct EffectSheetModifiers(
        IReadOnlyDictionary<byte, double> Flat, IReadOnlyDictionary<byte, double> Percent)
    {
        private static readonly Dictionary<byte, double> NoRows = new();

        /// <summary>No active sheet row.</summary>
        public static EffectSheetModifiers None => new(NoRows, NoRows);

        /// <summary>True when no active effect has a row on a trait the sheet carries.</summary>
        public bool IsEmpty => Flat.Count == 0 && Percent.Count == 0;
    }

    /// <summary>
    /// A trait the 2498 carries: a primary (0..5), max HP/SP (21/22), or a derived stat in
    /// <see cref="TraitPackets.DerivedStatBlobOffsets"/>. The other traits the stat-modifier rows name
    /// (6/7 walk and move speed, 69, 76-78, 99, 104, …; about half of the rows) have no slot in the
    /// sheet and are left out of it.
    /// </summary>
    /// <remarks>Miss_Rate (35) is a sheet trait for its Trait_Kind 4 rows only (<see cref="MissRateBlobOffset"/>); see
    /// <see cref="IsSheetRow"/>.</remarks>
    private static bool IsSheetTrait(uint traitId)
        => traitId <= LastPrimaryTraitId || traitId is MaxHpTraitId or MaxSpTraitId
            || (traitId <= byte.MaxValue && TraitPackets.DerivedStatBlobOffsets.ContainsKey((byte)traitId));

    /// <summary>
    /// A stat-modifier row the 2498 carries: a row on a <see cref="IsSheetTrait"/> trait, a Trait_Kind 4 Miss_Rate row
    /// (the u16 at <see cref="MissRateBlobOffset"/>), or a Trait_Kind 3 row on one of the six rate traits 32 / 33 / 34 /
    /// 56 / 57 / 95, which <see cref="PcTraitRates.Stamp"/> adds to its rate slot (kind-4 rows on them are not carried
    /// and stay out).
    /// </summary>
    private static bool IsSheetRow(uint kind, uint traitId)
        => IsSheetTrait(traitId) || (traitId == MissRateTraitId && kind == PercentModifierKind)
            || (kind == FlatModifierKind && TraitRates.IsRateTrait(traitId));

    /// <summary>
    /// Sums the sheet rows of <paramref name="effects"/>: for each effect, every <see cref="ModifierIndex"/>
    /// row on a sheet trait, valued exactly as its 2499 line (<c>Basic_Add_Value + Level_Multiply_Value ×
    /// EffectLevel</c>, see <see cref="BuildTraitChangeLines"/>), added per (kind, trait) across effects.
    /// </summary>
    /// <remarks>
    /// ADDED, not multiplied, across effects: effect 6310 (kind 4, traits 23/25, 1000.0) applied over the same
    /// rows of 40012 takes the stat from base × 1.1 to base × 1.2, not × 1.21; and 105206's +5% STR over an active
    /// +10% takes STR from 261 × 1.10 to 261 × 1.15. Summing gives the expected value within one; compounding
    /// does not.
    /// </remarks>
    internal EffectSheetModifiers SumSheetModifiers(IEnumerable<ActiveEffect> effects)
    {
        Dictionary<byte, double>? flat = null, percent = null;
        foreach (var effect in effects)
        {
            if (!ModifierIndex.TryGetValue(effect.EffectId, out var mods))
                continue;
            foreach (var m in mods)
            {
                if (!IsSheetRow(m.Kind, m.TraitId))
                    continue;
                float value = m.BasicAdd + m.LevelMul * effect.EffectLevel;
                var bucket = m.Kind == FlatModifierKind ? (flat ??= new()) : (percent ??= new());
                var trait = (byte)m.TraitId;
                bucket[trait] = bucket.GetValueOrDefault(trait) + value;
            }
        }

        return flat is null && percent is null
            ? EffectSheetModifiers.None
            : new EffectSheetModifiers(flat ?? EffectSheetModifiers.None.Flat, percent ?? EffectSheetModifiers.None.Percent);
    }

    // =============================================================================================
    // EXP-gain rate (Exp_Rate, trait 69) of the active effects
    // =============================================================================================

    /// <summary>Trait id of the EXP-gain rate: TraitBasicInfo TraitID 69 <c>Exp_Rate</c>. Its Effect_Table rows
    /// are Trait_Kind 4 (<see cref="PercentModifierKind"/>, 1/10,000), Task_Name 2: 71 effects, among them the
    /// EXP manuals 102001-102003 (Level_Multiply 2000 / 5000 / 5000), the VVIP buffs 105001-105060 (3000) and the
    /// guild skill 40009 (Level × 100). 142 ItemInfo rows grant one as Effect_ID_1.</summary>
    internal const uint ExpRateTraitId = 69;

    /// <summary>
    /// The EXP-gain rate <paramref name="player"/>'s active effects add, in 1/10,000 (3000.0 = +30%): the sum
    /// of <see cref="SumExpRate"/> over <c>PlayerState.ActiveEffects</c>.
    /// </summary>
    internal double ExpRateOf(PlayerState player) => SumExpRate(ActiveEffectsOf(player));

    /// <summary>
    /// Sums the Trait_Kind 4 / trait 69 (<see cref="ExpRateTraitId"/>) rows of <paramref name="effects"/>, each
    /// valued exactly as the 2499 line <see cref="BuildTraitChangeLines"/> sends for it
    /// (<c>Basic_Add_Value + Level_Multiply_Value × EffectLevel</c>), ADDED across effects.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The 2429's blob+16 is
    /// <c>(uint)((float)Reward_EXP × (1f + (float)Σrate / 10000f + e/10000f))</c> in single precision (see
    /// <see cref="ApplyExpRate"/>), where Σrate is the sum of the kind 4 / trait 69 2499 lines of the effects
    /// active on the player and e is the calendar bonus below. The rates ADD across effects: a 276,000 Reward_EXP under rates 1,000 + 2,000 + 3,000 pays 276,000 × 1.6 = 441,600.
    /// </para>
    /// <para>
    /// e = 1 (+0.01%, e.g. 180,000 -> 180,018) is not an effect: it
    /// follows the calendar date, a server-wide bonus on a schedule rather than a player state. That schedule
    /// is in no loaded table, so it is not modelled. Guild skill effects
    /// (40009, the most frequent line) are not applied to players by this server, so they never enter
    /// <c>ActiveEffects</c> and add nothing here. Equipment traits (TraitInfo Trait_Kind 1 / Trait_Type 69)
    /// are not in this sum either: they are not effects.
    /// </para>
    /// </remarks>
    internal double SumExpRate(IEnumerable<ActiveEffect> effects) => SumRate(effects, ExpRateTraitId);

    /// <summary>Trait id of the profession-EXP rate: <c>ProJobExp_Rate</c>, trait 76, Trait_Kind 4 (1/10,000). Effect_Table
    /// has it on 63 rows, 61 of them at 3,000, among them every VVIP effect 105001-105060, next to their trait-69
    /// Exp_Rate 3,000.</summary>
    internal const uint ProJobExpRateTraitId = 76;

    /// <summary>
    /// The profession-EXP rate <paramref name="player"/>'s active effects add, in 1/10,000: the trait-76 sum
    /// (<see cref="SumRate"/>), applied to a gather's 2526 gain with <see cref="ApplyExpRate"/>
    /// (FuncObjectActionService). A gather's gain is Action_Value1 or floor(Action_Value1 x 1.3) and nothing else,
    /// and the multiplier is per session, the shape of a 3,000 effect. Which effect gives it is not known.
    /// </summary>
    internal double ProJobExpRateOf(PlayerState player) => SumRate(ActiveEffectsOf(player), ProJobExpRateTraitId);

    /// <summary>
    /// The evasion rate <paramref name="player"/>'s active effects add, in 1/10,000 (10,000 = +100%): the Trait_Kind 4
    /// trait-35 Miss_Rate sum (<see cref="SumRate"/>). The map-move buff 60205 carries +10,000 (see
    /// <see cref="ApplyMapMoveBuffAsync"/>), as do the other '무적'-type effects 4301, 42061, 53052 and 60206; NpcCombatService
    /// makes a monster hit on a PC at <see cref="FullMissRate"/> or more a miss.
    /// </summary>
    internal double MissRateOf(PlayerState player) => SumRate(ActiveEffectsOf(player), MissRateTraitId);

    /// <summary>A <see cref="MissRateOf"/> of +100% (10,000 in 1/10,000): every hit misses.</summary>
    internal const double FullMissRate = PercentModifierScale;

    /// <summary>
    /// Sums the Trait_Kind 4 rows of trait <paramref name="traitId"/> over <paramref name="effects"/>, each valued as
    /// its 2499 line (<c>Basic_Add_Value + Level_Multiply_Value × EffectLevel</c>), added across effects. For trait 69
    /// this is <see cref="SumExpRate"/>, whose remarks hold the measurement of the rule.
    /// </summary>
    internal double SumRate(IEnumerable<ActiveEffect> effects, uint traitId)
    {
        double rate = 0;
        foreach (var effect in effects)
        {
            if (!ModifierIndex.TryGetValue(effect.EffectId, out var mods))
                continue;
            foreach (var m in mods)
            {
                if (m.Kind == PercentModifierKind && m.TraitId == traitId)
                    rate += m.BasicAdd + m.LevelMul * effect.EffectLevel;
            }
        }
        return rate;
    }

    /// <summary>
    /// <paramref name="baseExp"/> raised by <paramref name="expRate"/> (1/10,000, from <see cref="SumExpRate"/>)
    /// in SINGLE-precision arithmetic: <c>(uint)((float)baseExp × (1f + (float)expRate / 10000f))</c>,
    /// every step rounded to a float and the product truncated, clamped to 0..uint.MaxValue
    /// (276,000 at 6,000 -> 441,600; 1,810,000 at 6,400 -> 2,968,400; 4,889,000 at 5,900 -> 7,773,509, where exact
    /// arithmetic gives 7,773,510). A rate at or below -100% gives 0.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The calendar bonus (see <see cref="SumExpRate"/>, not modelled) is added to the float multiplier as a further
    /// <c>+ 1f/10000f</c>. The exact <c>floor(baseExp × (10000 + rate) / 10000)</c> is 1 EXP off in cases this
    /// form gets right.
    /// </para>
    /// <para>
    /// The ORDER is part of the rule: the rates are summed first, then divided, then added to 1f. Every other
    /// order tried reproduces a strict subset of the cases: the multiplier formed in double and rounded once,
    /// <c>(float)((10000 + rate) / 10000)</c>; accumulating <c>m += rate_i / 10000f</c> effect by effect; the exact
    /// floor. With no rate active the multiplier is exactly 1f and Reward_EXP (at most 6,830,000 in Quest_Info,
    /// below 2^24) converts to a float exactly, so nothing changes there. The Effect_Table rates are whole numbers,
    /// so the (float) of the double sum is exact.
    /// </para>
    /// </remarks>
    internal static uint ApplyExpRate(long baseExp, double expRate)
    {
        if (baseExp <= 0)
            return 0;
        // Each (float) cast forces the rounding to single precision at that step, as the client rounds it; without
        // them the language allows an intermediate to be kept at a higher precision.
        float fraction = (float)((float)expRate / (float)PercentModifierScale);
        float multiplier = (float)(1f + fraction);
        float boosted = (float)((float)baseExp * multiplier);
        if (float.IsNaN(boosted) || boosted <= 0f)
            return 0;
        // (float)uint.MaxValue is 2^32, so any boosted below it truncates into range.
        return boosted >= uint.MaxValue ? uint.MaxValue : (uint)boosted;
    }

    /// <summary>
    /// The 2498 for a PC in world with <paramref name="modifiers"/> folded in. Primaries go through the
    /// same bucket as the gear (so they flow into max HP/SP and the derived stats through the class
    /// formula, as a gear primary does); max HP/SP and the derived stats then get
    /// <see cref="FoldSheetModifiers"/>. With no modifier it equals
    /// <c>WorldEntryService.BuildFullPcTrait2498</c> over the same gear.
    /// </summary>
    /// <remarks>
    /// <para>
    /// PRIMARIES CASCADE. The ranking buff 50603 (kind 4, 1000.0 on traits 0-4), applied over 40011/40012, raises
    /// the primaries by 10% and, with them, max HP/SP and every stat the class formula derives from a primary, and
    /// the max HP / attack percent of 40011/40012 applies on top. A flat primary adds to
    /// the gear flat and a percent primary to the gear percent (<c>(base + flat) × (1 + percent)</c>,
    /// EquipStats.Apply), which is also how the client combines them (105206's +5% STR over +10%, see
    /// <see cref="SumSheetModifiers"/>). The client keeps the primaries fractional (111.1) and this server
    /// whole, so a cascaded value can differ by one.
    /// </para>
    /// <para>
    /// THE FRAME. WorldEntryService keeps its donor frame private. BuildFullPcTrait2498 with no store
    /// and no gear returns that frame with only the fields that BuildPcTrait2498FromDonor rewrites on every
    /// call changed (the vitals, the primaries, the bonus columns and point pool, blob+68 from the donor), so it
    /// is used as the donor here, and every other byte is the donor's, as in the plain sheet.
    /// </para>
    /// </remarks>
    internal static byte[] BuildPcTraitSheet(Character character, GameDataStore store, EquipStats gear,
        int currentHp, int currentSp, EffectSheetModifiers modifiers)
    {
        var flat = new Dictionary<byte, int>(gear.Flat);
        var percent = new Dictionary<byte, float>(gear.Percent);
        foreach (var (trait, value) in modifiers.Flat)
        {
            if (trait <= LastPrimaryTraitId)
                flat[trait] = flat.GetValueOrDefault(trait) + (int)Math.Round(value, MidpointRounding.ToEven);
        }
        foreach (var (trait, value) in modifiers.Percent)
        {
            // EquipStats.Percent is in percentage points (TraitInfo Kind 2 / PercentTraitDivisor), and a
            // Trait_Kind 4 value is on the same 1000 = +10% scale.
            if (trait <= LastPrimaryTraitId)
                percent[trait] = percent.GetValueOrDefault(trait) + (float)(value / CharacterStats.PercentTraitDivisor);
        }

        var frame = WorldEntryService.BuildFullPcTrait2498(character, null, null, currentHp, currentSp);
        var sheet = WorldEntryService.BuildPcTrait2498FromDonor(frame, character,
            new EquipStats(flat, percent), store, (currentHp, currentSp));
        var folded = FoldSheetModifiers(sheet, modifiers, currentHp, currentSp);
        // The rate slots again, now from the consts the fold moved and with the Trait_Kind 3 rate lines added
        // (PcTraitRates: e.g. 4125 alone -> 1000).
        PcTraitRates.Stamp(folded, character.Level, modifiers.Flat);
        return folded;
    }

    /// <summary>
    /// Applies the non-primary rows of <paramref name="modifiers"/> to a 143-byte 2498 body: max HP
    /// (blob+8), max SP (blob+12) and each <see cref="TraitPackets.DerivedStatBlobOffsets"/> stat become
    /// <c>round((value + flat) × (10000 + percent) / 10000)</c>, half to even, saturated to the field's
    /// width; the Trait_Kind 4 Miss_Rate sum is added raw to the u16 at blob+64 (<see cref="MissRateBlobOffset"/>);
    /// then the current HP/SP (blob+72/+76) are <paramref name="currentHp"/>/<paramref name="currentSp"/>
    /// cut to the new maxima. The primaries (blob+14..+24) are not touched: they cascade, and
    /// <see cref="BuildPcTraitSheet"/> applies them before this.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Applying this to the previous 2498 gives the next one when the effect set changes only non-primary stats
    /// that carried no row before. Small deviations remain: the client keeps a fractional base (2,106 × 1.1 lands
    /// as 2,316), and some sheets carry a percent that no 2499 line shows (a flat +1,000 max HP landing as +1,200,
    /// from a passive source).
    /// </para>
    /// <para>
    /// FLAT INSIDE THE PERCENT: a flat max HP row (60037's +1,000, 104001's +3,000) raises max HP by exactly
    /// flat × (1 + that percent): +1,200 under +20%, +1,350 under +35%, +3,750 under +25%, …. HALF TO EVEN: 40012
    /// takes blob+44 2,955 -> 3,250 (3,250.5). SATURATED: 6310 takes blob+44 62,035 -> 65,535, the u16 ceiling
    /// (62,035 / 1.1 × 1.2 would be 67,675).
    /// </para>
    /// </remarks>
    internal static byte[] FoldSheetModifiers(ReadOnlySpan<byte> sheetBody, EffectSheetModifiers modifiers,
        int currentHp, int currentSp)
    {
        if (sheetBody.Length != TraitPackets.PcTraitBodyLen)
        {
            throw new ArgumentException(
                $"a 2498 body is {TraitPackets.PcTraitBodyLen} bytes; got {sheetBody.Length}", nameof(sheetBody));
        }

        var body = sheetBody.ToArray();
        var blob = body.AsSpan(TraitPackets.PcTraitBodyLen - TraitPackets.PcTraitBlobLen);   // skip 02 8C 00

        uint maxHp = BinaryPrimitives.ReadUInt32LittleEndian(blob[TraitPackets.PcTraitMaxHpOffset..]);
        ushort maxSp = BinaryPrimitives.ReadUInt16LittleEndian(blob[TraitPackets.PcTraitMaxSpOffset..]);
        maxHp = (uint)ScaleStat(maxHp, modifiers, MaxHpTraitId, uint.MaxValue);
        maxSp = (ushort)ScaleStat(maxSp, modifiers, MaxSpTraitId, ushort.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(blob[TraitPackets.PcTraitMaxHpOffset..], maxHp);
        BinaryPrimitives.WriteUInt16LittleEndian(blob[TraitPackets.PcTraitMaxSpOffset..], maxSp);

        foreach (var (statId, offset) in TraitPackets.DerivedStatBlobOffsets)
        {
            ushort v = BinaryPrimitives.ReadUInt16LittleEndian(blob[offset..]);
            BinaryPrimitives.WriteUInt16LittleEndian(blob[offset..], (ushort)ScaleStat(v, modifiers, statId, ushort.MaxValue));
        }

        // Miss_Rate: the Trait_Kind 4 trait-35 sum added RAW to the u16 at blob+64, saturated (MissRateBlobOffset). It is
        // a rate slot, not a stat scaled by a percent: ScaleStat would leave a 0 base at 0.
        if (modifiers.Percent.TryGetValue(MissRateTraitId, out var missRate))
        {
            int baseRate = BinaryPrimitives.ReadUInt16LittleEndian(blob[MissRateBlobOffset..]);
            double raised = Math.Round(baseRate + missRate, MidpointRounding.ToEven);
            BinaryPrimitives.WriteUInt16LittleEndian(blob[MissRateBlobOffset..], (ushort)Math.Clamp(raised, 0d, ushort.MaxValue));
        }

        // curHp <= maxHp always (see TraitPackets.BuildPcTrait2498).
        BinaryPrimitives.WriteUInt32LittleEndian(blob[TraitPackets.PcTraitCurHpOffset..],
            (uint)Math.Min((long)Math.Max(0, currentHp), maxHp));
        BinaryPrimitives.WriteUInt16LittleEndian(blob[TraitPackets.PcTraitCurSpOffset..],
            (ushort)Math.Min(Math.Max(0, currentSp), (int)maxSp));
        return body;
    }

    /// <summary><c>round((value + flat) × (10000 + percent) / 10000)</c>, half to even, in [0, max]; the
    /// value unchanged when no row names <paramref name="traitId"/>. Exact in a double for whole inputs.</summary>
    private static long ScaleStat(long value, EffectSheetModifiers modifiers, byte traitId, long max)
    {
        bool hasFlat = modifiers.Flat.TryGetValue(traitId, out var flat);
        bool hasPercent = modifiers.Percent.TryGetValue(traitId, out var percent);
        if (!hasFlat && !hasPercent)
            return value;

        double scaled = (value + flat) * (PercentModifierScale + percent) / PercentModifierScale;
        return (long)Math.Clamp(Math.Round(scaled, MidpointRounding.ToEven), 0d, max);
    }

    // =============================================================================================
    // Timer: expiry + periodic ticks
    // =============================================================================================

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Effect service started (tick: {Ms}ms)", TickMs);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(TickMs));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await ProcessEffectsAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Effect service tick failed");
            }
        }
    }

    private async Task ProcessEffectsAsync()
    {
        var now = DateTime.UtcNow;
        await ProcessPlayerEffectsAsync(now);
        await ProcessNpcEffectsAsync(now);
    }

    private async Task ProcessPlayerEffectsAsync(DateTime now)
    {
        foreach (var player in _players.GetAllPlayers())
        {
            List<ActiveEffect>? expired = null;
            List<ActiveEffect>? ticks = null;

            lock (player.ActiveEffects)
            {
                if (player.ActiveEffects.Count == 0)
                    continue;

                for (int i = player.ActiveEffects.Count - 1; i >= 0; i--)
                {
                    var e = player.ActiveEffects[i];
                    if (e.ExpiresAt is { } t && t <= now)
                    {
                        if (IsLastTickDue(e, t))
                            (ticks ??= new List<ActiveEffect>()).Add(e);
                        (expired ??= new List<ActiveEffect>()).Add(e);
                        player.ActiveEffects.RemoveAt(i);
                        continue;
                    }

                    // Advance NextTickAt UNDER THE LOCK so a due tick cannot fire twice.
                    if (e.IsPeriodic && now >= e.NextTickAt)
                    {
                        AdvanceTick(e, now);
                        (ticks ??= new List<ActiveEffect>()).Add(e);
                    }
                }
            }

            if (ticks != null)
                foreach (var e in ticks)
                {
                    // A tick that kills (KillByDotAsync) removes the victim's After_Death_Handling 0 effects
                    // (DeathEffects), and a 1463 cancel can land while this loop awaits: a queued tick whose effect has
                    // left ActiveEffects in the meantime does not tick on the corpse. An effect that expired in this
                    // pass left the list above and still gets its last tick. Ticks on corpses come from effects that
                    // were still active (see TickPlayerEffectAsync).
                    if (!StillTicks(player, e, expired))
                        continue;
                    await TickPlayerEffectAsync(player, e);
                }

            if (expired != null)
                foreach (var e in expired)
                    await RemoveEffectAsync(player, e.EffectId, new[] { e });
        }
    }

    /// <summary>
    /// True when the tick <paramref name="e"/>, queued by this pass, should still run: the effect is still in
    /// <c>ActiveEffects</c> (by reference, under its lock), or it is one of this pass's own expiries
    /// (<paramref name="expired"/>), which leave the list before their last tick.
    /// </summary>
    internal static bool StillTicks(PlayerState player, ActiveEffect e, List<ActiveEffect>? expired)
    {
        if (expired != null)
            foreach (var x in expired)
                if (ReferenceEquals(x, e))
                    return true;
        lock (player.ActiveEffects)
        {
            foreach (var a in player.ActiveEffects)
                if (ReferenceEquals(a, e))
                    return true;
        }
        return false;
    }

    private async Task ProcessNpcEffectsAsync(DateTime now)
    {
        foreach (var handle in _npcEffects.Keys)
        {
            if (!_npcEffects.TryGetValue(handle, out var list))
                continue;

            var npc = _npcState.GetNpc(handle);
            if (npc == null || npc.IsDead)
            {
                // The monster is gone or was killed by another source: drop its whole effect set.
                _npcEffects.TryRemove(handle, out _);
                continue;
            }

            List<ActiveEffect>? expired = null;
            List<ActiveEffect>? ticks = null;

            lock (list)
            {
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    var e = list[i];
                    if (e.ExpiresAt is { } t && t <= now)
                    {
                        if (IsLastTickDue(e, t))
                            (ticks ??= new List<ActiveEffect>()).Add(e);
                        (expired ??= new List<ActiveEffect>()).Add(e);
                        list.RemoveAt(i);
                        continue;
                    }

                    if (e.IsPeriodic && now >= e.NextTickAt)
                    {
                        AdvanceTick(e, now);
                        (ticks ??= new List<ActiveEffect>()).Add(e);
                    }
                }
            }

            if (ticks != null)
            {
                foreach (var e in ticks)
                {
                    var died = await TickNpcEffectAsync(npc, e);
                    if (died)
                    {
                        // A DoT killed it: the death path is done and every effect is moot.
                        _npcEffects.TryRemove(handle, out _);
                        expired = null;
                        break;
                    }
                }
            }

            if (expired != null)
                foreach (var e in expired)
                    await RemoveNpcEffectAsync(e.MapId, handle, e.EffectId);

            // An emptied-but-still-alive bucket is left in place: it is tiny, is reused if the same
            // monster is targeted again (GetOrAdd returns it), and is removed wholesale by the
            // dead/gone branch above once the monster dies or despawns. Racing a TryRemove against a
            // concurrent apply here would risk losing a just-added effect, so it is deliberately not
            // pruned mid-life.
        }
    }

    // ---- Periodic tick schedule -----------------------------------------------------------------

    /// <summary>
    /// Moves a due tick's <see cref="ActiveEffect.NextTickAt"/> one period on FROM ITS OWN DUE TIME, so the
    /// ticks stay one period apart however late the 1 s loop runs; after a stall that left it more than a
    /// period behind, from now, so the backlog is not replayed as a burst. Called under the list's lock.
    /// </summary>
    /// <remarks>
    /// The client expects ticks one period apart plus a few ms.
    /// Moving
    /// the next tick on from "now" made each due time carry this pass's own latency, so the pass one period
    /// later fired it only when it ran at least as late; otherwise the tick slipped a whole loop second and
    /// a 2 s effect ticked after 3 s.
    /// </remarks>
    private static void AdvanceTick(ActiveEffect e, DateTime now)
    {
        var next = e.NextTickAt.AddMilliseconds(e.TickIntervalMs);
        e.NextTickAt = next > now ? next : now.AddMilliseconds(e.TickIntervalMs);
    }

    /// <summary>
    /// Whether an effect found expired still owes the tick that fell due at or before its expiry
    /// <paramref name="expiresAt"/>. The last tick is inside the duration: a 10.1 s effect with a 2 s period
    /// ticks at 2, 4, 6, 8 and 10 s. On a 1 s loop the pass that sees the 10 s tick due usually also sees the
    /// 10.1 s expiry, and taking the expiry first dropped that fifth tick. Called under the list's lock.
    /// </summary>
    private static bool IsLastTickDue(ActiveEffect e, DateTime expiresAt)
        => e.IsPeriodic && e.NextTickAt <= expiresAt;

    // ---- Periodic tick application --------------------------------------------------------------

    /// <summary>
    /// The 2495 blob+20 value for a tick whose server-side HP delta is <paramref name="hpPerTick"/>.
    /// The wire carries the OPPOSITE sign: positive is damage, negative is a heal.
    /// </summary>
    /// <remarks>
    /// The wire value is the opposite of the server-side sign: <c>hp_next == hp_prev − blob+20</c>. This path used
    /// to pass <c>HpPerTick</c> straight through (negative for a DoT), so every damage tick was shown as a heal.
    /// The amount is the nominal per-tick value, not the clamped one: a heal tick that leaves a full HP bar
    /// unchanged is still reported at its full amount.
    /// </remarks>
    private static int DotWireAmount(int hpPerTick) => -hpPerTick;

    /// <summary>
    /// One DoT/HoT tick on a PLAYER: applies the signed per-tick HP/SP delta to the player's vitals
    /// (a tick that takes HP to 0 kills, <see cref="KillByDotAsync"/>; SP clamped to [0, MaxMp]) and broadcasts 2495 (and 2496 whenever the effect carries an SP
    /// amount, with the nominal amount even when the clamp swallowed it) to the player's map. On a
    /// dead player the vitals are left alone and only the 2495 goes out.
    /// </summary>
    /// <remarks>
    /// A dead player keeps HP 0 and SP 0 through every tick, whatever its sign: ticks that land on a PC between its
    /// 2391 and its revival (damage or heals) all report HP 0 and SP 0 at blob+24/+28. The old code
    /// ran Math.Max(1, 0 + HpPerTick) on the corpse, which set it to 1 HP (a HoT healed it) while
    /// IsDead stayed true, and the 2495 then reported HP 1. <c>Hp &lt;= 0</c> also counts as dead:
    /// NpcCombatService writes HP 0 before it sets IsDead, and RegenService uses the same test.
    /// The tick still goes out. Its blob+20 is the nominal amount, as for a
    /// live target, and blob+24/+28 carry the corpse's current 0/0. No 2496 is sent, because SP does
    /// not move.
    /// </remarks>
    private async Task TickPlayerEffectAsync(PlayerState player, ActiveEffect e)
    {
        bool dead = player.IsDead || player.Hp <= 0;

        if (e.HpPerTick < 0 && !dead && player.Hp + e.HpPerTick <= 0
            && ResolveDotDeathRunner(player) is { } combat)
        {
            await KillByDotAsync(player, e, combat);
            return;
        }

        // Apply the tick's HP/SP as one step under the vitals lock, re-checking death there: a HoT must not heal
        // (resurrect) a player a combat/fall/DoT hit has just killed off another thread, and a DoT applied here must
        // not lose a concurrent lethal write. The damage branch still floors at 1 (lethal HP goes only through
        // KillByDotAsync above); the heal caps at MaxHp and the SP clamps to [0, MaxMp], exactly as before.
        if (!dead)
        {
            lock (player.Vitals)
            {
                if (!player.IsDead && player.Hp > 0)
                {
                    if (e.HpPerTick != 0)
                    {
                        var newHp = e.HpPerTick < 0
                            ? Math.Max(DotHpFloor, player.Hp + e.HpPerTick)                 // damage; lethal only via KillByDotAsync
                            : Math.Min(player.MaxHp, player.Hp + e.HpPerTick);     // heal: capped at max
                        if (newHp != player.Hp)
                            player.ApplyHpDelta(newHp - player.Hp);
                    }

                    // SP is clamped to [0, MaxMp], so on a full or nearly full bar the tick moves SP by less
                    // than SpPerTick, or not at all. The 2496 below still announces the NOMINAL amount (see
                    // there); only the resulting SP it carries at blob+24 reflects the clamp.
                    if (e.SpPerTick != 0)
                        player.ApplySpDelta(e.SpPerTick);
                }
            }
        }

        if (e.HpPerTick != 0)
        {
            var body = SkillDamagePackets.BuildDotDamageNotify2495(
                victimHandle: player.EntityId,
                sourceHandle: e.CasterHandle,
                skillId: e.SourceSkillId,
                damage: DotWireAmount(e.HpPerTick),
                victimCurHp: (uint)Math.Max(0, player.Hp),
                victimCurSp: (uint)Math.Max(0, player.Mp));
            await _broadcast.BroadcastToMapAsync(player.MapId, ZoneS2COpcodes.OnDotDamageNotify,
                w => w.WriteBytes(body));
        }

        // 2496 blob+12 is the SOURCE ACTOR handle and blob+24 the target's RESULTING SP — never the
        // effect id, and never 0. This call used to pass e.EffectId into blob+12 and let blob+24
        // default to 0; both parameters are uint, so it compiled silently and went out wrong.
        //   blob+12 is actor-TAGGED (0x11 PC, 0x13 NPC, ...) and usually equals blob+8, i.e. the caster is
        //           usually the target itself. An effect id would carry no actor tag and would not track blob+8.
        //   blob+24 is a live gauge value, almost never 0: the client paints it into the SP gauge, so a 0 empties
        //           the SP bar of every client that sees the tick.
        //   blob+20 is the NOMINAL per-tick amount, not the clamped delta, and it goes out even when
        //           the clamp swallowed the whole tick. The client does not recompute the gauge from blob+20: it
        //           SETS the current SP from blob+24 and hands blob+20 to the actor as the floating "+N SP"
        //           number. So on a full bar "+N SP" shows every tick. The HP side (2495, DotWireAmount)
        //           already follows the same nominal rule.
        //           blob+20 is never zero, and it cannot be here either: this runs only
        //           when SpPerTick != 0.
        if (e.SpPerTick != 0 && !dead)
            await AnnounceAbnormalSpAsync(player.MapId, player.EntityId, e.CasterHandle, e.SpPerTick,
                (ushort)Math.Clamp(player.Mp, 0, ushort.MaxValue));
    }

    /// <summary>
    /// The NpcCombatService that runs a player's death, or null when this tick must not kill: the container holds
    /// none (unit tests), or <paramref name="player"/> is in a started field duel. NpcCombatService is the
    /// <c>AddSingleton&lt;NpcCombatService&gt;()</c> registration in Program.cs, resolved through the scope factory
    /// so this constructor (and its registration) stays as it is, as NpcCombatService resolves this service for
    /// MissRateOf. Without it the tick keeps the old floor at 1 HP, because a 0-HP player that never got its 2391
    /// would have no death UI and so no way to send 1372 ReqPCRevival.
    /// </summary>
    /// <remarks>
    /// A started duel ends fought-out, never in a death (FieldPvPCombat): a DoT-lethal duel end is
    /// that path, which this server does not run for a DoT, so a duelist keeps the floor at 1 HP.
    /// </remarks>
    private NpcCombatService? ResolveDotDeathRunner(PlayerState player)
    {
        if (_scopeFactory is null)
            return null;
        if (_players is not null && FieldPvPService.StartedDuelOf(player.EntityId, _players) is not null)
            return null;
        using var scope = _scopeFactory.CreateScope();
        return scope.ServiceProvider.GetService<NpcCombatService>();
    }

    /// <summary>
    /// A DoT tick that takes a PLAYER to 0 HP: the player dies, and the death runs as a monster kill's does with the
    /// lethal 2495 in place of the 2696 (<see cref="NpcCombatService.RunDotDeathAsync"/>). The dead state (HP 0, SP 0,
    /// IsDead, the engagement dropped) is set before anything is sent, as NpcCombatService and FallDamageHandler set
    /// it, so no other pass sees a 0-HP player as alive.
    /// </summary>
    /// <remarks>
    /// The death sequence: the death head
    /// (Illusion Pyramid 29012 close, the death-wear 2503s, the Khara 2574/2577 death count, the 2581 failures), the
    /// state-6 2398, the lethal 2495 (victim HP 0 and SP 0), then 2391 + 2836 + 2837 to the victim, the removal of its
    /// After_Death_Handling 0 effects (2498 [2315] 2493 2494 each, ascending id; <see cref="DeathEffects"/>, run by
    /// NpcCombatService.SendPlayerDeathAsync as on a monster kill), the state-6 2398 again, the victim's flag-2 2630
    /// removals, the 2392 and the monsters' leave-combat 2398s. The 2392 names the DoT's CASTER as killer at blob+12
    /// (a 0x13 monster, or a 0x11 PC, the victim itself included) and carries the tick's damage at blob+16. Some DoT
    /// deaths open with a 2938 and a state-0 2398; the monster-kill path sends neither, and the two paths are kept
    /// the same. No 2496 goes out on the lethal tick: SP is 0 with the death. The effects the death kept
    /// (After_Death_Handling 1) keep ticking on the corpse afterwards (the dead branch of
    /// <see cref="TickPlayerEffectAsync"/>); a tick of a removed effect still queued in the same pass is skipped
    /// (<see cref="StillTicks"/>).
    /// </remarks>
    private async Task KillByDotAsync(PlayerState player, ActiveEffect e, NpcCombatService combat)
    {
        // Transition to dead atomically, and ONLY if a concurrent combat/fall hit has not already killed the player:
        // this death sequence must fire exactly once (SetVitalsDead sets HP 0, SP 0, IsDead).
        lock (player.Vitals)
        {
            if (player.IsDead)
                return;
            player.SetVitalsDead();
        }
        AutoAttackState.Disengage(player);

        int damage = DotWireAmount(e.HpPerTick);
        var lethalTick = SkillDamagePackets.BuildDotDamageNotify2495(
            victimHandle: player.EntityId,
            sourceHandle: e.CasterHandle,
            skillId: e.SourceSkillId,
            damage: damage,
            victimCurHp: 0,
            victimCurSp: 0);
        try
        {
            await combat.RunDotDeathAsync(player, e.CasterHandle, damage,
                () => _broadcast.BroadcastToMapAsync(player.MapId, ZoneS2COpcodes.OnDotDamageNotify,
                    w => w.WriteBytes(lethalTick)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Name}: the DoT death sequence failed", player.Name);
        }
    }

    /// <summary>
    /// One DoT/HoT tick on a MONSTER: damage goes through <c>NpcStateManager.ApplyDamage</c> (the
    /// existing vitals path, which also banks threat and marks death), a heal caps HP at max, and the
    /// tick is broadcast with 2495. NO 2496 — a monster has no modelled SP, so its resulting-SP
    /// field cannot be filled honestly; see the comment below. Returns <c>true</c> when the tick
    /// killed the monster, in which case the shared loot/death path has already run.
    /// </summary>
    private async Task<bool> TickNpcEffectAsync(NpcRuntimeState npc, ActiveEffect e)
    {
        bool died = false;

        if (e.HpPerTick < 0)
        {
            (_, died) = _npcState.ApplyDamage(npc.EntityOid, -e.HpPerTick, e.CasterHandle, e.CasterName);
        }
        else if (e.HpPerTick > 0)
        {
            // Under the same HpLock ApplyDamage uses, and never on a dead monster: a HoT tick must not raise the HP
            // of a monster a concurrent killing hit has just zeroed (which would resurrect the corpse).
            lock (npc.HpLock)
            {
                if (!npc.IsDead)
                    npc.Hp = Math.Min(npc.MaxHp, npc.Hp + e.HpPerTick);
            }
        }

        if (e.HpPerTick != 0)
        {
            var body = SkillDamagePackets.BuildDotDamageNotify2495(
                victimHandle: npc.EntityOid,
                sourceHandle: e.CasterHandle,
                skillId: e.SourceSkillId,
                damage: DotWireAmount(e.HpPerTick),
                victimCurHp: (uint)Math.Max(0, npc.Hp),
                // blob+28 is written into the VICTIM's SP by the client, so 0 is not neutral: NPC-victim 2495s carry
                // a non-zero SP, one value per handle, i.e. the tick restates the SP the client already
                // holds: what our 2506 record put at +54 (NpcSpawnService.WriteNpcRecord). That is
                // SkillDamagePackets.NpcTargetSp, the one source 2718 / 2797 / 2643 use too, so they cannot
                // drift apart.
                victimCurSp: SkillDamagePackets.NpcTargetSp(npc));
            await _broadcast.BroadcastToMapAsync(npc.MapId, ZoneS2COpcodes.OnDotDamageNotify,
                w => w.WriteBytes(body));
        }

        // No 2496 for a MONSTER target — not because it is never sent for monsters, but because WE cannot fill
        // it honestly. blob+24 is the target's resulting CURRENT SP and NpcRuntimeState models no
        // SP at all — it has Hp/MaxHp and nothing else — so the only value available is 0, which
        // writes an empty SP gauge into the monster's client-side stat block. Inventing the number instead
        // is what this repo forbids. This path used to send exactly that 0.
        // The HP side of the tick is unaffected: 2495 above already carried it. Wire an SP model
        // onto NpcRuntimeState and this becomes a normal AnnounceAbnormalSpAsync call.

        if (died)
            await HandleNpcDotKillAsync(npc, e.CasterHandle);

        return died;
    }

    /// <summary>
    /// Broadcasts 2496 OnChangeAbnormalSP for one SP change to the target's map.
    /// </summary>
    /// <remarks>
    /// ⚠️ The parameters mirror <see cref="CombatSkillNotifyPackets.BuildChangeAbnormalSP2496"/>
    /// exactly, because they used to NOT: this helper passed the EFFECT ID into what the builder
    /// reads as blob+12 (the source actor handle) and left blob+24 at its 0 default. Both
    /// arguments are <c>uint</c>, so the mismatch compiled silently and only showed on the wire.
    /// </remarks>
    /// <param name="targetHandle">Actor whose SP changed — blob+8.</param>
    /// <param name="sourceHandle">Actor that caused it (the caster) — blob+12.</param>
    /// <param name="spDelta">SERVER-side signed NOMINAL per-tick amount (negative = drain), not the
    /// clamped delta: the full amount is announced on a full bar. The builder inverts it for the
    /// wire.</param>
    /// <param name="targetCurSp">The target's SP AFTER the change — blob+24, the value the client
    /// paints into the SP gauge.</param>
    private Task AnnounceAbnormalSpAsync(uint mapId, uint targetHandle, uint sourceHandle,
        int spDelta, ushort targetCurSp)
    {
        var body = CombatSkillNotifyPackets.BuildChangeAbnormalSP2496(
            targetHandle: targetHandle,
            sourceHandle: sourceHandle,
            spDelta: spDelta,
            targetCurSp: targetCurSp);
        return _broadcast.BroadcastToMapAsync(mapId, ZoneS2COpcodes.OnChangeAbnormalSP,
            w => w.WriteBytes(body));
    }

    /// <summary>
    /// A DoT tick brought a monster to 0 HP. Reuse the EXACT loot/death sequence a skill or normal
    /// attack kill uses — the credit (<c>LootService.ProcessKillCreditAsync</c>: quest ticks, Khara, EXP),
    /// 2471 OnLootingAuthority unicast to the killer, then the corpse (<c>ProcessNpcCorpseAsync</c>: 2392
    /// OnDeadNotify, drops, the deferred 2507 OnNPC_Out) — so a DoT kill is a first-class kill. If the caster cannot be resolved to a
    /// live player session (e.g. an NPC-applied DoT, or the caster logged out) the monster is still
    /// marked dead by <c>ApplyDamage</c> and will respawn on its timer; no death broadcast is faked.
    /// </summary>
    private async Task HandleNpcDotKillAsync(NpcRuntimeState npc, uint casterHandle)
    {
        PlayerState? killer = null;
        foreach (var p in _players.GetAllPlayers())
        {
            if (p.EntityId == casterHandle) { killer = p; break; }
        }

        if (killer == null)
        {
            _logger.LogDebug("DoT killed NPC {Npc} but caster {Caster:X8} is not a live player; no loot grant",
                npc.EntityOid, casterHandle);
            return;
        }

        var session = _broadcast.GetSession(killer.SessionId);
        if (session == null)
            return;

        await _loot.ProcessKillCreditAsync(session, killer, npc.EntityOid, npc.NpcId);
        await session.SendInternalRmiAsync(ZoneS2COpcodes.OnLootingAuthority,
            CombatPackets.BuildLootingAuthority2471(npc.EntityOid));
        await _loot.ProcessNpcCorpseAsync(session, killer, npc.EntityOid, npc.NpcId);
    }

    // ---- Removal --------------------------------------------------------------------------------

    /// <summary>
    /// Removes <paramref name="effectId"/> from <paramref name="player"/> at the player's own request
    /// (1463 ReqCancelBuff) and announces it with the same 2498 -> 2493 -> 2494 sequence an expiry uses
    /// (<see cref="RemoveEffectAsync"/>). Returns <c>false</c>, sending nothing, when the player does not
    /// have that effect or the data does not let a player delete it.
    /// </summary>
    /// <remarks>
    /// The effect must leave <c>ActiveEffects</c> here: while it stays there the tick loop keeps
    /// applying its per-tick HP/SP delta and later "expires" it a second time.
    /// <para>
    /// Staying silent on a miss is a design choice. The silence rests on our own state: this service is the
    /// server's only 2492 sender, so a client holds exactly the effects in <c>ActiveEffects</c>, and an effect
    /// missing here has already been removed with its own 2493/2494 (or was never granted). A second 2494 would
    /// reach every client on the map.
    /// </para>
    /// <para>
    /// Only effects whose Effect_Table <c>Picking_Delete_Type</c> is 1 can be cancelled this way — the
    /// request names the effect, so without the check a client could strip a debuff such as a monster
    /// DoT (those rows carry 0). The gate rests mostly on the column itself; it fails safe, because a
    /// refused cancel leaves the effect in place on both sides.
    /// </para>
    /// </remarks>
    public async Task<bool> CancelPlayerEffectAsync(PlayerState player, uint effectId)
    {
        if (!IsPlayerCancellable(effectId))
            return false;

        var removed = TakeEffects(player, effectId);
        if (removed.Count == 0)
            return false;

        await RemoveEffectAsync(player, effectId, removed);
        return true;
    }

    /// <summary>
    /// Removes <paramref name="effectId"/> from a PC because the SERVER ends it — a warp departure dropping its
    /// Warp_Handling 0 effects (WarpDeparture), a dismount ending effect 100 (RidingService) — through the same
    /// <see cref="RemoveEffectAsync"/> run an expiry uses: 2498, 2493, 2494, no 2499. Returns false, sending nothing,
    /// when the effect is not active.
    /// </summary>
    /// <remarks>
    /// No Picking_Delete_Type gate, unlike <see cref="CancelPlayerEffectAsync"/>: that column says what a CLIENT may
    /// ask to delete, and server-side triggers remove effects players may not cancel. A departure removes
    /// Warp_Handling 0 effects, most of them 60037/60043 (fortress occupation), 40013 (SiegeWeapon) and the 2100xx
    /// pets, whose Picking_Delete_Type is 0 (WarpDeparture has the list).
    /// </remarks>
    internal async Task<bool> RemovePlayerEffectAsync(PlayerState player, uint effectId)
    {
        var removed = TakeEffects(player, effectId);
        if (removed.Count == 0)
            return false;

        await RemoveEffectAsync(player, effectId, removed);
        return true;
    }

    /// <summary>Takes every entry of <paramref name="effectId"/> out of <c>ActiveEffects</c> under its lock and returns
    /// them (empty when the effect is not active), so the removal can tell what its sheet lost
    /// (<see cref="RemovalMovesMax"/>).</summary>
    private static List<ActiveEffect> TakeEffects(PlayerState player, uint effectId)
    {
        lock (player.ActiveEffects)
        {
            var taken = player.ActiveEffects.FindAll(e => e.EffectId == effectId);
            if (taken.Count > 0)
                player.ActiveEffects.RemoveAll(e => e.EffectId == effectId);
            return taken;
        }
    }

    /// <summary>Effect_Table <c>Picking_Delete_Type</c> of the effects a player may cancel by clicking them off.</summary>
    private const byte PickingDeleteTypePlayerCancellable = 1;

    /// <summary>Effect_Table <c>Picking_Delete_Type == 1</c> for this id (the column is identical on
    /// every row of an id). Unknown ids are not cancellable.</summary>
    private bool IsPlayerCancellable(uint effectId)
    {
        foreach (var row in _data.EffectTable)
        {
            if (row.ID == effectId)
                return row.Picking_Delete_Type == PickingDeleteTypePlayerCancellable;
        }
        return false;
    }

    /// <summary>
    /// Removes one effect from a PC subject in the client's order: the recomputed 2498 OnPcTrait sheet and
    /// 2493 OnDelEffectSuccess, both unicast to the owner (2493 carries no handle and is applied to the
    /// local player), then 2494 OnDelEffect broadcast to the map with the owner included. No 2499. When the
    /// removed effect moves the PC's max HP or max SP (<see cref="RemovalMovesMax"/>), the PC's 2315 goes to the map
    /// between the 2498 and the 2493 (<see cref="ResendViewDataAfterMaxChangeAsync"/>). <paramref name="removed"/> is
    /// what the caller took out of <c>ActiveEffects</c> for <paramref name="effectId"/>.
    /// Used for an expiry and for a cancel (1463 ReqCancelBuff) alike.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ORDER. Every PC removal is a 2493 immediately followed by its 2494, with a 2498 before the 2493 and no
    /// other effect message between (at most a 2497 speed change or a 2315). That holds for effects with and
    /// without stat-modifier rows, and for cancels alone: 2498 -> 2493 -> 2494.
    /// </para>
    /// <para>
    /// NO 2499 ON REMOVAL. Cancelled effects carry no 2499, and a 2499 following a removal shortly after is a
    /// re-application (a non-zero value, followed by a 2492 of that effect). This used to
    /// send one value-0 2499 per modifier row after the 2494. It is unnecessary: the client's 2494 handler
    /// erases the effect's lines from its modifier vector by effect id, so the zero
    /// lines only re-inserted dead entries after the 2494 had cleared them.
    /// </para>
    /// </remarks>
    private async Task RemoveEffectAsync(PlayerState player, uint effectId, IReadOnlyList<ActiveEffect> removed)
    {
        var session = _broadcast.GetSession(player.SessionId);
        if (session != null)
        {
            // The sheet first. The effect has already left ActiveEffects, so this is the sheet without it
            // and with every effect still active — the expected value (see SendPcTraitSheetAsync).
            var worn = await SendPcTraitSheetAsync(player, session);

            // Then the PC's 2315 when the removed effect moves its max HP or max SP. See RemovalMovesMax and
            // ResendViewDataAfterMaxChangeAsync.
            if (worn != null && player.Character is { } character && RemovalMovesMax(player, character, worn, removed))
                await ResendViewDataAfterMaxChangeAsync(player, character, worn);

            await session.SendInternalRmiAsync(ZoneS2COpcodes.OnDelEffectSuccess,
                EffectPackets.BuildDelEffectSuccess2493(effectId));
        }

        // 2494: effect id FIRST, handle SECOND — the reverse of 2492. See BuildDelEffect2494.
        var body = ActorVitalsPackets.BuildDelEffect2494(effectId, player.EntityId);
        await _broadcast.BroadcastToMapAsync(player.MapId, ZoneS2COpcodes.OnDelEffect,
            w => w.WriteBytes(body));

        _logger.LogDebug("Removed effect {EffectId} from {Name}", effectId, player.Name);
    }

    /// <summary>
    /// Whether taking <paramref name="removed"/> away moves the PC's max HP or max SP: the max of the sheet over the
    /// gear <paramref name="worn"/> and the effects still in <c>ActiveEffects</c>, against the same sheet with
    /// <paramref name="removed"/> folded back in. Both are built here from one snapshot, so the answer depends on the
    /// effect's rows and the PC's sheet only.
    /// </summary>
    /// <remarks>
    /// <para>
    /// NOT <c>PlayerState.MaxHp/MaxMp</c> BEFORE AND AFTER THE 2498. That pair says whether the server's stored max
    /// moved, and the stored max is only the sheet's when the last writer was a sheet refresh: a max seeded from
    /// anything else (a 1304 not yet run, a value set by hand) made a removal of an effect with no max row (100
    /// riding, 60205 map-move buff, 40013 SiegeWeapon, whose Effect_Table rows hold no trait 21/22 value) look like a
    /// max change and sent a 2315 that is not wanted for them.
    /// </para>
    /// <para>
    /// PER EFFECT. A burst is removed one effect at a time, and the 2315 sits on the removal
    /// of each effect with a max row and on no other (e.g. 40012: 2498 2493 2494, then 40011: 2498 2315 2493 2494,
    /// then 40010/40009/40000 with none). The expiry loop here takes a
    /// whole burst out of <c>ActiveEffects</c> at once, so the stored max has already dropped by the first removal;
    /// asking each effect against the still-active set keeps the 2315 on the same removals.
    /// </para>
    /// </remarks>
    private bool RemovalMovesMax(PlayerState player, Character character, IReadOnlyList<InventoryItem> worn,
        IReadOnlyList<ActiveEffect> removed)
    {
        if (removed.Count == 0)
            return false;
        var still = ActiveEffectsOf(player);
        var after = SheetMax(character, worn, player, SumSheetModifiers(still));
        var before = SheetMax(character, worn, player, SumSheetModifiers(still.Concat(removed)));
        return after != before;
    }

    /// <summary>Max HP (blob+8, u32) and max SP (blob+12, u16) of the sheet with <paramref name="modifiers"/>.</summary>
    private (uint MaxHp, ushort MaxSp) SheetMax(Character character, IReadOnlyList<InventoryItem> worn,
        PlayerState player, EffectSheetModifiers modifiers)
    {
        var body = BuildSheet(character, worn, player.Hp, player.Mp, modifiers);
        var blob = body.AsSpan(TraitPackets.PcTraitBodyLen - TraitPackets.PcTraitBlobLen);
        return (BinaryPrimitives.ReadUInt32LittleEndian(blob[TraitPackets.PcTraitMaxHpOffset..]),
            BinaryPrimitives.ReadUInt16LittleEndian(blob[TraitPackets.PcTraitMaxSpOffset..]));
    }

    /// <summary>
    /// The PC's 2315 OnPCViewData record, broadcast to its map with the PC included, sent by a removal whose effect
    /// moves the PC's max HP or max SP (<see cref="RemovalMovesMax"/>). Goes between that 2498 and the 2493.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE TRIGGER IS THE MAX CHANGE, NOT A LIST OF EFFECTS. A removal whose sheet moves the max HP (blob+8) or max
    /// SP (blob+12) carries the PC's 2315 between its 2498 and the 2493, and removals that leave the max
    /// unchanged do not. By effect, every one with a trait 21/22 (max HP/SP) row sends it, the speed and stun
    /// effects do not, and the primary-stat effects split by whether the stat moves max HP/SP (the ranker buff
    /// 50603, +10 % on STR/DEX/INT/WIS/VIT and no trait 21/22 row, sends it; 6319, 72013 and 115 do not), hence a
    /// test on the resulting max and not on the effect's rows. The removals this server makes that hit it are the
    /// item buffs (ItemInfo Effect_ID_1: 105001/105010/105202/105207) and the ranker buffs of group 50600; effect
    /// 100 (riding) and 60205 (map-move buff) have no max row and send none.
    /// </para>
    /// <para>
    /// NONE ON AN ADD. An add of a trait 21/22 effect is not preceded by a 2315, so the apply paths send none.
    /// </para>
    /// <para>
    /// TO THE MAP, OWNER INCLUDED. Self gets it (above), and so do the viewers: a removal from another PC is
    /// preceded by that PC's 2315.
    /// </para>
    /// <para>
    /// The record is the one the re-dress handlers send (ActorStatePackets.BuildOnPCViewData2315): max HP/SP from
    /// PlayerState.MaxHp/MaxMp, which the sheet has just set, and the VEC23 effect list from ActiveEffects, which no
    /// longer holds the removed effect. The gear is the list the 2498 was built from.
    /// Several effects expiring in one tick leave ActiveEffects together, so the first removal's 2498 already carries
    /// the burst's final max. The 2315 still goes on the removal of each effect that moves the max and on no other,
    /// as one-by-one removals place it (e.g. the guild buffs ending: 2498 2493 2494 for 40012, then 2498 2315 2493
    /// 2494 for 40011, then 40010/40009/40000 with none): same opcode sequence and the same end state on the client;
    /// only the max the burst's first 2498 states differs.
    /// </para>
    /// <para>
    /// NOT SENT HERE: the 2497 that usually follows this 2315 (2498, 2315, [2559], 2497, 2493, 2494) belongs to
    /// the speed rows of the same effects (60037/60043), and this server sends no 2497 for an effect's speed rows.
    /// </para>
    /// </remarks>
    private async Task ResendViewDataAfterMaxChangeAsync(PlayerState player, Character character,
        IReadOnlyList<InventoryItem> worn)
    {
        var viewData = ActorStatePackets.BuildOnPCViewData2315(player, character, worn, _emblems);
        await _broadcast.BroadcastToMapAsync(player.MapId, ZoneS2COpcodes.OnPCViewData,
            w => w.WriteBytes(viewData));
    }

    /// <summary>
    /// Removes one effect from a MONSTER: 2494 OnDelEffect broadcast to the map only. There is no
    /// 2493 for an NPC — that message is applied to the local player by the client and carries no
    /// handle, so a broadcast copy would corrupt every other client's state.
    /// </summary>
    private async Task RemoveNpcEffectAsync(uint mapId, uint npcHandle, uint effectId)
    {
        var body = ActorVitalsPackets.BuildDelEffect2494(effectId, npcHandle);
        await _broadcast.BroadcastToMapAsync(mapId, ZoneS2COpcodes.OnDelEffect,
            w => w.WriteBytes(body));

        _logger.LogDebug("Expired effect {EffectId} on NPC {Npc}", effectId, npcHandle);
    }
}
