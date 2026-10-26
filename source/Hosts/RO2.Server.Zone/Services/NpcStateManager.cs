using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using RO2.GameData;

using RO2.Worlds.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Runtime state for a single spawned NPC entity.
/// </summary>
public class NpcRuntimeState
{
    public uint EntityOid { get; set; }
    public uint NpcId { get; set; }
    public uint MapId { get; set; }
    public int Hp { get; set; }
    public int MaxHp { get; set; }

    /// <summary>
    /// The NPC's max SP, which is also its current SP (NPCs spend no SP), at record+50 and +54 (u16). Set by
    /// <see cref="NpcStateManager"/> when the spawn is tracked (<c>ResolveVitals</c>: the value from
    /// <see cref="NpcRetailVitals"/>, else a stated fallback). 0 means it was never resolved (a state built
    /// outside the manager), and then <see cref="WireMaxSp"/> keeps the old HP mirror.
    /// </summary>
    public int MaxSp { get; set; }

    /// <summary>
    /// record+54 max SP (and the SP 2643 / 2718 / 2797 / 2495 restate): <see cref="MaxSp"/> clamped to 16 bits,
    /// or for a state whose SP was never resolved the old mirror of max HP, clamped the same way.
    /// </summary>
    internal ushort WireMaxSp => (ushort)Math.Clamp(MaxSp > 0 ? MaxSp : MaxHp, 0, ushort.MaxValue);

    /// <summary>
    /// record+50 current SP: equal to <see cref="WireMaxSp"/> once <see cref="MaxSp"/> is resolved (cur SP
    /// == max SP, see <see cref="MaxSp"/>), else the old mirror of current HP.
    /// </summary>
    internal ushort WireCurSp => (ushort)Math.Clamp(MaxSp > 0 ? MaxSp : Hp, 0, ushort.MaxValue);
    public int Atk { get; set; }
    public int Def { get; set; }
    public int ExpReward { get; set; }
    public int Camp { get; set; }

    /// <summary>
    /// Whether this NPC's corpse can be gathered: <c>NPCInfo.AfterDeath_FuncObject_ID != 0</c> for its asset, set
    /// when the spawn is tracked. It goes on the wire at record+66 of the shared 2506/2317/2319 NPC record
    /// (NpcSpawnService.WriteNpcRecord). It rides on the state, not on a
    /// lookup, because the record writer is static. A state built outside the manager keeps false, which was
    /// the byte every record carried before.
    /// </summary>
    public bool CorpseGatherable { get; set; }

    /// <summary>NPC level. Goes on the wire at 2506 record offset +8.</summary>
    public int Level { get; set; }
    public bool IsDead { get; set; }
    public DateTime? DeathTime { get; set; }

    /// <summary>
    /// Serialises the HP read-modify-write and the death transition: <see cref="NpcStateManager.ApplyDamage"/>
    /// (any combat handler thread) and <see cref="NpcStateManager.Respawn"/>. Under it, only the first hit across
    /// HP 0 sets <see cref="IsDead"/> and reports the kill. Lock order is HpLock then <see cref="ThreatLock"/>, never
    /// the reverse.
    /// </summary>
    internal readonly Lock HpLock = new();

    /// <summary>
    /// Horizontal reach this NPC lands a hit from: NPCInfo.Attack_Range, else the default melee reach
    /// (<see cref="NpcCombatService.Reach"/>) when the row has none. Set by
    /// <see cref="NpcStateManager"/> when the spawn is tracked; 0 means a state built outside the manager, which
    /// <see cref="NpcCombatService"/> reads as the melee fallback. It is the NPC's own property: the client's
    /// range test takes the range from the ATTACKER, never from the victim's weapon. No tolerance or
    /// target-size term is added to it.
    /// </summary>
    public float AttackReach { get; set; }

    public SpawnEntry Spawn { get; set; } = null!;

    /// <summary>
    /// PathEngine navmesh cell this NPC stands on, or <c>null</c> when none is known for
    /// this spawn. Goes on the wire at 2506 record offset +17 (and its copy at +29). NOT a height —
    /// see NpcSpawnService.BuildOnNpcIn2506 for why that distinction is load-bearing.
    /// </summary>
    public int? NavCell { get; set; }

    /// <summary>Live X, which drifts from <see cref="SpawnEntry.PosX"/> once the NPC wanders.</summary>
    public float PosX { get; set; }

    /// <summary>Live Y, which drifts from <see cref="SpawnEntry.PosY"/> once the NPC wanders.</summary>
    public float PosY { get; set; }

    /// <summary>When this NPC may next pick a wander destination.</summary>
    public DateTime NextMoveAt { get; set; }

    /// <summary>
    /// The last 2318 OnMoveNPC walk this NPC was sent on (origin, destination, speed, start), or <c>null</c>
    /// when it has not moved since it spawned or respawned. <see cref="PosX"/>/<see cref="PosY"/> already hold
    /// that walk's destination; this is what lets a 2319 OnNPCViewData report the point ON the walk while it is
    /// still being covered (NpcSpawnService.BuildOnNpcViewData2319). Set by NpcMovementService right after
    /// the 2318 broadcast, cleared by its respawn pass. An immutable record, swapped whole, so a 1306 handler
    /// thread reading it never sees half a walk.
    /// </summary>
    public NpcMove? LastMove { get; set; }

    // ---- Threat / aggro (ThreatAggro system) ------------------------------------------------
    //
    // A monster keeps a THREAT TABLE: each attacker's accumulated threat, and the name to show on
    // the client's aggro meter. The current target is the highest-threat attacker, kept until another
    // row passes it by more than 10% (NpcStateManager.SelectAggroTarget); that target is what
    // 2630/2629 body+4 currentTargetHandle reports. Friendly NPCs (Camp <= 1) never
    // build threat.

    /// <summary>Guards <see cref="Threat"/> and <see cref="AggroTargetHandle"/>: threat is written
    /// from combat handler threads (via <see cref="NpcStateManager.ApplyDamage"/>) and read/written
    /// from the game-loop thread (NpcCombatService), so all access is serialised on this lock.</summary>
    internal readonly Lock ThreatLock = new();

    /// <summary>attackerHandle -> accumulated threat row. Keyed by the attacker's actor handle
    /// (a PC handle, low byte 0x11). Cleared on death and respawn.</summary>
    internal readonly Dictionary<uint, NpcStateManager.ThreatRow> Threat = new();

    /// <summary>The attacker the monster is currently on: the highest-threat entry unless the previous
    /// target is within 10% of it (see NpcStateManager.SelectAggroTarget), or 0 when the threat table is
    /// empty. This is the value that goes out as 2630/2629 body+4.</summary>
    public uint AggroTargetHandle { get; internal set; }

    // ---- AI: proximity aggression + leash (server-side, custom; see NpcAggression) ------------------
    //
    // A monster with an aggression mode other than None opens a fight with a player who enters its
    // SightRadius (NpcCombatService's acquisition scan), on the condition the mode sets (always, or
    // while the player is not too far above it in level). A fight the monster carries too far from its
    // spawn (> LeashBound) is dropped: the monster heals and returns. Both are 0/None for every asset
    // unless the custom table says otherwise, which is the passive default.

    /// <summary>How this monster starts fights on its own: <see cref="NpcAggressionMode.None"/> (passive, the
    /// default) unless the custom <see cref="NpcAggression"/> table gives its asset a mode. Friendly NPCs
    /// (Camp &lt;= 1) are always None.</summary>
    public NpcAggressionMode Aggression { get; set; }

    /// <summary>The level gap for a <see cref="NpcAggressionMode.ByLevel"/> monster: it acquires a player while
    /// <c>playerLevel - Level &lt;= AggressionLevelGap</c>.</summary>
    public int AggressionLevelGap { get; set; }

    /// <summary>Horizontal radius this monster acquires a player within (NPCInfo.Reaction_Bound). 0 means the row
    /// carried none, and the acquisition scan then uses its melee fallback.</summary>
    public float SightRadius { get; set; }

    /// <summary>How far from its spawn this monster chases before it leashes and resets (NPCInfo.Trace_Bound). 0
    /// means the row carried none, so distance never leashes it (it still resets when it has no live target).</summary>
    public float LeashBound { get; set; }

    /// <summary>
    /// While <see cref="DateTime.UtcNow"/> is before this, the monster has just reset and is running home: it is
    /// INVULNERABLE — hits on it miss (damageKind 2) and bank no threat, so it cannot be pulled back into the
    /// fight before it arrives. Set by <see cref="NpcStateManager.LeashReset"/> (and extended by the chase's
    /// walk-home). Default (0) = not returning.
    /// </summary>
    public DateTime ReturningUntil { get; set; }

    /// <summary>True while the monster is in its post-reset return/invulnerable window.</summary>
    public bool IsReturning(DateTime now) => now < ReturningUntil;
}

/// <summary>
/// Manages runtime HP/state for all spawned NPC entities on a map.
/// Thread-safe singleton. Tracks damage, death, and respawn timers.
/// </summary>
public class NpcStateManager
{
    private readonly ConcurrentDictionary<uint, NpcRuntimeState> _npcs = new();

    /// <summary>
    /// Corpses that outlived their NPC's respawn: handle -> a dead snapshot of the NPC as it died. The
    /// respawn gives the live NPC a fresh handle (<see cref="Respawn"/>), and this is what the old handle
    /// still resolves to (<see cref="GetNpc"/>) until the corpse's own 2507 has gone out
    /// (<see cref="RetireCorpse"/>). Never in <see cref="_npcs"/>, so no tick, respawn or damage sees it.
    /// </summary>
    private readonly ConcurrentDictionary<uint, NpcRuntimeState> _corpses = new();

    /// <summary>Serialises <see cref="Respawn"/> against <see cref="InitializeMap"/>: both change which
    /// handle a spawn slot has, and a map load in the middle of a re-key would add the slot a second time.</summary>
    private readonly Lock _lifecycleGate = new();

    private readonly GameDataStore _data;
    private readonly ILogger<NpcStateManager> _logger;

    /// <summary>Default respawn delay for killed NPCs.</summary>
    private static readonly TimeSpan RespawnDelay = TimeSpan.FromSeconds(30);

    // Stand-in stats for a spawn whose asset has no NpcServerInfo / NPCInfo row. They are placeholders, not game
    // data, and apply only to that missing-row case.

    /// <summary>Camp assumed when neither table has a row: a friendly NPC.</summary>
    private const int DefaultCamp = 1;

    /// <summary>Level assumed when neither table has a row.</summary>
    private const int DefaultLevel = 1;

    /// <summary>Max HP assumed when NpcServerInfo has no row.</summary>
    private const int DefaultHp = 100;

    /// <summary>ATK assumed when NpcServerInfo has no row.</summary>
    private const int DefaultAtk = 10;

    /// <summary>DEF assumed when NpcServerInfo has no row.</summary>
    private const int DefaultDef = 5;

    /// <summary>Experience reward assumed when NpcServerInfo has no row.</summary>
    private const int DefaultExpReward = 10;

    /// <summary>
    /// Highest camp value of a friendly NPC. A camp above it is a monster: only monsters take damage, keep a
    /// threat table and aggro.
    /// </summary>
    private const int MaxFriendlyCamp = 1;

    /// <summary>How long after a reset a monster stays invulnerable while it runs home (hits miss); the chase's
    /// walk-home may extend <see cref="NpcRuntimeState.ReturningUntil"/> to the real travel time.</summary>
    private static readonly TimeSpan ReturnInvulnDuration = TimeSpan.FromSeconds(3);

    // ---- Threat / aggro (ThreatAggro system) ------------------------------------------------

    /// <summary>One row of a monster's threat table: an attacker, the name shown on the aggro
    /// meter, and the accumulated threat. Threat is banked as a <see cref="long"/> so a long fight
    /// never overflows; it is clamped to <see cref="uint"/> only when written to the wire.</summary>
    public sealed class ThreatRow
    {
        public uint AttackerHandle;
        public string Name = string.Empty;
        public long Threat;
    }

    /// <summary>
    /// A queued 2630 NfyAggroChanged: one positive threat bank on an NPC (every hit with an attacker, see
    /// <see cref="AddThreatCore"/>). NpcCombatService drains this on the game loop and broadcasts the
    /// 2630 to the NPC's map. The state manager stays transport-free — it owns the WHAT, the combat
    /// service owns the WHEN and the wire — mirroring how NpcMovementService/NpcSpawnService split.
    /// </summary>
    /// <param name="NpcHandle">The monster whose threat table changed (low byte 0x13).</param>
    /// <param name="MapId">The map the monster is on, so the broadcast reaches the right players.</param>
    /// <param name="CurrentTarget">The monster's current top-threat target PC, or 0 if none.</param>
    /// <param name="PcHandle">The attacker whose threat just changed (low byte 0x11).</param>
    /// <param name="PcName">That attacker's character name (goes out UTF-16LE).</param>
    /// <param name="Threat">That attacker's accumulated threat after the change (clamped to u32).</param>
    public readonly record struct AggroChange(uint NpcHandle, uint MapId, uint CurrentTarget,
        uint PcHandle, string PcName, uint Threat);

    /// <summary>Pending 2630 broadcasts, drained by NpcCombatService's tick.</summary>
    private readonly ConcurrentQueue<AggroChange> _aggroChanges = new();

    /// <summary>Dequeue the next queued aggro change to broadcast, if any.</summary>
    public bool TryDequeueAggroChange(out AggroChange change) => _aggroChanges.TryDequeue(out change);

    public NpcStateManager(GameDataStore data, ILogger<NpcStateManager> logger)
    {
        _data = data;
        _logger = logger;
    }

    /// <summary>
    /// The NPC vitals table (<see cref="NpcRetailVitals.Shipped"/>, data/npc/NpcRetailVitals.json), or null
    /// when the file is not there, in which case every NPC takes the fallback of <see cref="ResolveVitals"/>. A test
    /// sets its own table.
    /// </summary>
    internal NpcRetailVitals? RetailVitals { get; init; } = NpcRetailVitals.Shipped;

    /// <summary>
    /// The custom per-asset aggression table (<see cref="NpcAggression.Shipped"/>, custom/data/NpcAggression.json),
    /// or null when the file is not there, in which case every spawned monster is passive (<see
    /// cref="NpcAggressionMode.None"/>) and only retaliates. A test injects its own table.
    /// </summary>
    internal NpcAggression? AggressionTable { get; init; } = NpcAggression.Shipped;

    /// <summary>1 once the missing-table warning of <see cref="TrackSpawns"/> has been logged.</summary>
    private int _warnedNoRetailVitals;

    /// <summary>
    /// The (max HP, max SP) an NPC of <paramref name="asset"/> at <paramref name="level"/> spawns with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The table's pair when <see cref="RetailVitals"/> has that (asset, level): max HP and max SP are one
    /// fixed pair per (asset, level) (see <see cref="NpcRetailVitals"/>), which NpcServerInfo's HP does not follow.
    /// </para>
    /// <para>
    /// FALLBACK for an asset the table does not cover: HP stays <paramref name="tableHp"/>
    /// (NpcServerInfo.HP, a synthetic value), since HP depends on the asset, not only its level. SP takes the
    /// typical max SP of NPCs of that level (SP is close to a function of the level, see
    /// <see cref="NpcRetailVitals"/>), and with no data for that level either, the old HP mirror
    /// clamped to 16 bits.
    /// </para>
    /// </remarks>
    internal (int MaxHp, int MaxSp) ResolveVitals(uint asset, int level, int tableHp)
    {
        var table = RetailVitals;
        if (table is not null && table.TryGet(asset, level, out var hp, out var sp))
            return (hp, sp);
        if (table is not null && table.TryGetLevelMaxSp(level, out var levelSp) && levelSp > 0)
            return (tableHp, levelSp);
        return (tableHp, Math.Clamp(tableHp, 0, ushort.MaxValue));
    }

    /// <summary>
    /// Initialize all NPC states for a map. Called when a player enters a map
    /// that hasn't been initialized yet.
    /// </summary>
    public void InitializeMap(uint mapId) => TrackSpawns(mapId, _data.GetMapSpawns(mapId));

    /// <summary>
    /// <see cref="InitializeMap"/> with the map's ZoneSpawn rows in hand: one state per spawn slot not yet
    /// tracked, under <see cref="_lifecycleGate"/>. Internal so a test can give it a spawn row without loading a
    /// GameDataStore, whose load would swap the process-wide GameDataStore.Loaded under tests running in parallel.
    /// </summary>
    internal void TrackSpawns(uint mapId, SpawnEntry[] spawns)
    {
        if (spawns.Length == 0) return;
        // Said once: without the vitals table every NPC spawns with the synthetic NpcServerInfo HP and the HP
        // mirror for SP (ResolveVitals).
        if (RetailVitals is null && Interlocked.Exchange(ref _warnedNoRetailVitals, 1) == 0)
            _logger.LogWarning("{Path} is missing or unreadable: NPCs spawn with the NpcServerInfo HP fallback, not retail's vitals",
                NpcRetailVitals.ShippedPath);

        int added;
        lock (_lifecycleGate)
            added = AddMissingSpawns(mapId, spawns);

        if (added > 0)
            _logger.LogDebug("Initialized {Count} NPC states for map {MapId}", added, mapId);
    }

    /// <summary>The body of <see cref="TrackSpawns"/>, under its lock: one state per spawn slot not yet
    /// tracked. Returns how many were added.</summary>
    private int AddMissingSpawns(uint mapId, SpawnEntry[] spawns)
    {
        var added = 0;
        foreach (var spawn in spawns)
        {
            // Idempotent per (map, spawn index): re-entering a map finds the slot's current handle (a respawn
            // moves it to a fresh one, see Respawn).
            var entityOid = AllocateHandle(mapId, spawn.Index);

            // Skip if already tracked (another player already loaded this map)
            if (_npcs.ContainsKey(entityOid)) continue;

            var npcStats = _data.GetNpcStats(spawn.AssetIndex);
            var npcInfo = _data.Npcs.GetValueOrDefault(spawn.AssetIndex);
            var camp = npcStats?.Camp ?? npcInfo?.Camp ?? DefaultCamp;
            var level = npcStats?.Level ?? npcInfo?.Min_Level ?? DefaultLevel;
            // Aggression is the custom table's rule for this asset, but only a monster (Camp > 1) ever aggros;
            // a friendly NPC stays passive whatever the table says. The sight radius and leash come from the
            // shipped NPCInfo (Reaction_Bound / Trace_Bound).
            var aggroRow = camp > MaxFriendlyCamp && AggressionTable is { } table && table.TryGet(spawn.AssetIndex, out var ar)
                ? ar : default;
            // The table's vitals for this asset when it has them, else the stated fallback (see
            // ResolveVitals): NpcServerInfo.HP is synthetic.
            var (hp, sp) = ResolveVitals(spawn.AssetIndex, level, npcStats?.HP ?? DefaultHp);

            var state = new NpcRuntimeState
            {
                EntityOid = entityOid,
                NpcId = spawn.AssetIndex,
                MapId = mapId,
                Hp = hp,
                MaxHp = hp,
                MaxSp = sp,
                Atk = npcStats?.ATK ?? DefaultAtk,
                Def = npcStats?.DEF ?? DefaultDef,
                ExpReward = npcStats?.EXP_Reward ?? DefaultExpReward,
                Camp = camp,
                CorpseGatherable = (npcInfo?.AfterDeath_FuncObject_ID ?? 0) != 0,
                AttackReach = npcInfo is { Attack_Range: > 0f } ? npcInfo.Attack_Range : NpcCombatService.Reach,
                Level = level,
                IsDead = false,
                Spawn = spawn,
                NavCell = _data.GetNavCell(mapId, spawn.Index),
                PosX = spawn.PosX,
                PosY = spawn.PosY,
                Aggression = aggroRow.Mode,
                AggressionLevelGap = aggroRow.LevelGap,
                SightRadius = npcInfo?.Reaction_Bound ?? 0f,
                LeashBound = npcInfo?.Trace_Bound ?? 0f,
            };

            if (_npcs.TryAdd(entityOid, state))
                added++;
        }
        return added;
    }

    /// <summary>
    /// Get the runtime state for an NPC entity: the NPC the handle names, or, for the handle of a corpse
    /// whose NPC has already respawned under a fresh one, that corpse (dead, with its death stamp) until its
    /// 2507 has gone out. See <see cref="Respawn"/>.
    /// </summary>
    public NpcRuntimeState? GetNpc(uint entityOid)
    {
        if (_npcs.TryGetValue(entityOid, out var state)) return state;
        _corpses.TryGetValue(entityOid, out state);
        return state;
    }

    /// <summary>Threat granted per point of damage dealt when the caller names no amount (ApplyDamage's
    /// <c>threat</c>): the effect DoT path. The PC hit paths pass the computed amount instead
    /// (<see cref="ThreatAmount"/>), which for a normal hit with no stance effect is this same 1:1.</summary>
    private const long ThreatPerDamage = 1;

    /// <summary>
    /// Apply damage to an NPC. Returns actual damage dealt and whether the NPC died.
    /// </summary>
    /// <remarks>
    /// ADDITIVE threat hook (ThreatAggro): the damage numbers are UNCHANGED. When
    /// <paramref name="attackerHandle"/> is supplied (non-zero), the actual damage dealt is banked
    /// as threat proportional to that damage, and the monster re-selects its target (SelectAggroTarget). The
    /// two-argument call sites keep compiling unchanged (attacker defaults to 0 = no threat), so no
    /// existing damage path regresses. This is the precise "on each damage application, add threat"
    /// seam the combat handlers should feed with the attacker's handle and name.
    /// <para>
    /// <paramref name="threat"/>, when given, is the amount banked instead of the damage dealt: the PC hit paths pass
    /// <c>(damage + AddAggro) x stance</c> (<see cref="ThreatAmount"/>: the 1499 normal hit, the 1509 skill
    /// hit and the 1549 tick). Without it the bank is one point per point of damage dealt (the effect DoT path). A
    /// positive amount is banked even when the hit took no HP (a skill's AddAggro), never a zero or negative one.
    /// </para>
    /// </remarks>
    public (int DamageDealt, bool Died) ApplyDamage(uint entityOid, int damage,
        uint attackerHandle = 0, string? attackerName = null, long? threat = null)
    {
        if (!_npcs.TryGetValue(entityOid, out var state))
            return (0, false);

        // A monster running home after a reset is invulnerable: the hit misses (CombatService.ApplyDamageAsync
        // returns damageKind 2) and banks no threat here, so it is not pulled back before it arrives.
        if (state.IsReturning(DateTime.UtcNow))
            return (0, false);

        int actual;
        bool died = false;
        // The guard, the decrement and the death transition are ONE step under HpLock: two attackers racing across
        // HP 0 can no longer both see the NPC alive and both report the kill (a double kill credit / loot / 2471).
        // The threat bank stays inside it (HpLock -> ThreatLock) so a non-lethal hit cannot bank onto the table a
        // concurrent kill just cleared.
        lock (state.HpLock)
        {
            // A hit read through the old handle while Respawn moves the NPC to its fresh one lands on the corpse,
            // not on the respawned monster.
            if (state.IsDead || state.EntityOid != entityOid)
                return (0, false);

            actual = Math.Min(damage, state.Hp);
            state.Hp -= actual;

            // Bank the threat BEFORE the death branch so the killing blow is attributed too: the caller's amount
            // (ThreatAmount) or one point per point of damage dealt. Only monsters (Camp >= 2) keep a threat table.
            var banked = threat ?? (long)actual * ThreatPerDamage;
            if (attackerHandle != 0 && banked > 0 && state.Camp > MaxFriendlyCamp)
                AddThreatCore(state, attackerHandle, attackerName, banked);

            if (state.Hp <= 0)
            {
                state.Hp = 0;
                state.IsDead = true;
                state.DeathTime = DateTime.UtcNow;
                ClearThreat(state);
                died = true;
            }
        }

        if (died)
            _logger.LogDebug("NPC {NpcId} (entity: {Entity}) killed on map {Map}",
                state.NpcId, entityOid, state.MapId);
        return (actual, died);
    }

    /// <summary>
    /// Add threat from an attacker onto a monster's threat table and re-select its target
    /// (<see cref="SelectAggroTarget"/>). Every positive add queues one 2630 NfyAggroChanged (see
    /// <see cref="AddThreatCore"/>). Returns <c>true</c> when the aggro list changed shape (a new attacker
    /// joined or the target switched).
    /// </summary>
    /// <remarks>
    /// Safe to call from any thread. No-op for unknown, dead, or friendly (Camp &lt;= 1) NPCs, for a
    /// zero handle, or for a non-positive amount. This is the entry point NpcCombatService uses to
    /// feed threat from active engagement, and that <see cref="ApplyDamage"/> uses per hit.
    /// </remarks>
    public bool AddThreat(uint npcEntityOid, uint attackerHandle, string? attackerName, long amount)
    {
        if (attackerHandle == 0 || amount <= 0)
            return false;
        if (!_npcs.TryGetValue(npcEntityOid, out var state) || state.IsDead || state.Camp <= MaxFriendlyCamp)
            return false;
        return AddThreatCore(state, attackerHandle, attackerName, amount);
    }

    /// <summary>
    /// Locked threat accumulation + target re-selection + change queueing. The caller has already
    /// established that <paramref name="state"/> is a live monster, and both entry points guarantee a
    /// non-zero attacker and a positive amount.
    /// </summary>
    /// <remarks>
    /// One 2630 is queued on EVERY call, carrying the attacker's new accumulated threat and the monster's
    /// current target. Most 2630s are the per-hit threat step: queuing only a new row or a target switch would
    /// leave an open aggro meter at the first hit's value. A step queued by a killing hit is dropped by the
    /// flush once the monster is dead (NpcCombatService), since no 2630 follows a kill.
    /// </remarks>
    private bool AddThreatCore(NpcRuntimeState state, uint attackerHandle, string? attackerName,
        long amount)
    {
        uint newTarget, oldTarget;
        long newThreat;
        bool isNew;

        lock (state.ThreatLock)
        {
            oldTarget = state.AggroTargetHandle;

            if (!state.Threat.TryGetValue(attackerHandle, out var row))
            {
                row = new ThreatRow { AttackerHandle = attackerHandle, Name = attackerName ?? string.Empty };
                state.Threat[attackerHandle] = row;
                isNew = true;
            }
            else
            {
                isNew = false;
                if (!string.IsNullOrEmpty(attackerName))
                    row.Name = attackerName;   // keep the freshest name for the meter
            }

            row.Threat += amount;
            newThreat = row.Threat;

            // The monster keeps its target until another row passes it by more than 10%.
            newTarget = SelectAggroTarget(state.Threat, oldTarget);
            state.AggroTargetHandle = newTarget;
        }

        _aggroChanges.Enqueue(new AggroChange(
            state.EntityOid, state.MapId, newTarget, attackerHandle,
            attackerName ?? string.Empty, (uint)Math.Clamp(newThreat, 0, uint.MaxValue)));
        return isNew || newTarget != oldTarget;
    }

    /// <summary>A challenger takes the target only above <c>TargetSwitchNumerator / TargetSwitchDenominator</c>
    /// (110%) of the current target's threat. See <see cref="SelectAggroTarget"/>.</summary>
    private const long TargetSwitchNumerator = 11;

    /// <inheritdoc cref="TargetSwitchNumerator"/>
    private const long TargetSwitchDenominator = 10;

    /// <summary>
    /// The monster's target after a threat change: the current target while it is on the table and no
    /// other row has more than 110% of its threat, otherwise the top row (0 for an empty table).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A target that is still on the table is kept: when another row's threat passes the target's by less than
    /// 10% the target stays, and it moves once a row passes it by more than 10%. The same rule is
    /// NpcCombatService's (DropFromThreatTablesAsync), which already keeps the target when another row leaves
    /// the table. Making the top row the target on every add would let two players trading the lead by a
    /// point flip the monster, and its 2630/2643, back and forth. The line is taken strictly (a challenger at
    /// exactly 110% does not switch).
    /// </para>
    /// <para>
    /// Caller holds <see cref="NpcRuntimeState.ThreatLock"/>. Ties for the top row go to the row met first.
    /// </para>
    /// </remarks>
    internal static uint SelectAggroTarget(IReadOnlyDictionary<uint, ThreatRow> table, uint currentTarget)
    {
        uint best = 0;
        long bestThreat = long.MinValue;
        foreach (var kv in table)
        {
            if (kv.Value.Threat > bestThreat)
            {
                bestThreat = kv.Value.Threat;
                best = kv.Key;
            }
        }
        if (best == 0)
            return 0;

        if (currentTarget != 0 && currentTarget != best && table.TryGetValue(currentTarget, out var current)
            && bestThreat * TargetSwitchDenominator <= current.Threat * TargetSwitchNumerator)
            return currentTarget;

        return best;
    }

    /// <summary>Empties a monster's threat table and drops its current target.</summary>
    private static void ClearThreat(NpcRuntimeState state)
    {
        lock (state.ThreatLock)
        {
            state.Threat.Clear();
            state.AggroTargetHandle = 0;
        }
    }

    /// <summary>
    /// Drops a monster out of a fight it is no longer in: empties its threat table and its target, and restores
    /// it to full HP. Returns true when anything changed (it held threat/aggro or was below full HP), so the
    /// caller only acts on a reset that happened. Lock order HpLock then ThreatLock, as <see cref="ApplyDamage"/>.
    /// No-op for an unknown or dead handle.
    /// </summary>
    /// <remarks>
    /// The full heal is this server's reset model: a monster that loses every attacker (they died, logged out or
    /// left the map) returns to full, as it would after leashing home. The leave-combat 2398 is NpcChatService's
    /// sweep, which fires once the target is 0 and no player is engaged. The heal is instant; a gradual NPC regen
    /// tick is a later refinement.
    /// </remarks>
    public bool LeashReset(uint entityOid)
    {
        if (!_npcs.TryGetValue(entityOid, out var state))
            return false;
        lock (state.HpLock)
        {
            if (state.IsDead || state.EntityOid != entityOid)
                return false;
            bool hadThreat;
            lock (state.ThreatLock)
            {
                hadThreat = state.AggroTargetHandle != 0 || state.Threat.Count > 0;
                state.Threat.Clear();
                state.AggroTargetHandle = 0;
            }
            bool changed = hadThreat || state.Hp < state.MaxHp;
            state.Hp = state.MaxHp;
            state.ReturningUntil = DateTime.UtcNow + ReturnInvulnDuration;   // invulnerable while it runs home
            return changed;
        }
    }

    /// <summary>
    /// Respawn a dead NPC under a FRESH handle, at full HP. The <see cref="NpcRuntimeState"/> object is the
    /// same one, re-keyed in place (its <see cref="NpcRuntimeState.EntityOid"/> changes), so a caller holding
    /// it announces the new handle; the spawn slot (<see cref="AllocateHandle"/>) moves to it too. Returns the
    /// new handle, or 0 when <paramref name="entityOid"/> is not a tracked NPC.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A RESPAWN NEVER REUSES A HANDLE. The respawned NPC is announced with a 2317 under a fresh handle, never
    /// the dead NPC's, and this can happen while the corpse is still on the client (its 2507 not yet received).
    /// </para>
    /// <para>
    /// So the corpse keeps the old handle: when its 2507 has not gone out yet
    /// (<see cref="LootService.CorpseOnClients"/>), a dead snapshot of the NPC stays reachable under it
    /// through <see cref="GetNpc"/> (IsDead, the death stamp), which is what the corpse loot, the party dice
    /// and a skill aimed at the corpse read, until LootService's despawn calls <see cref="RetireCorpse"/>.
    /// Its unlooted items keep their 120 s lifetime (GroundItemService); the respawn no longer ends them.
    /// </para>
    /// <para>
    /// Order, so no reader ever finds the old handle empty or alive: the corpse snapshot is registered first,
    /// then the live state leaves the old key, is reset and re-keyed, and only then does the snapshot go if
    /// the corpse turns out to have left already. A despawn releasing the corpse concurrently retires the
    /// snapshot itself (<see cref="RetireCorpse"/>), whichever of the two runs last.
    /// </para>
    /// </remarks>
    public uint Respawn(uint entityOid)
    {
        lock (_lifecycleGate)
        {
            if (!_npcs.TryGetValue(entityOid, out var state) || state.EntityOid != entityOid) return 0;

            var deathStamp = state.DeathTime;
            var corpse = state.IsDead && deathStamp is not null ? CorpseSnapshot(state) : null;
            if (corpse is not null)
                _corpses[entityOid] = corpse;
            _npcs.TryRemove(new KeyValuePair<uint, NpcRuntimeState>(entityOid, state));

            var fresh = NextHandle();
            // One step under HpLock with the hit path, so a damage thread already holding this state sees either the
            // dead old life (and is refused by the handle check) or the fresh one, never a half-reset NPC.
            lock (state.HpLock)
            {
                state.EntityOid = fresh;
                state.Hp = state.MaxHp;
                state.IsDead = false;
                state.DeathTime = null;
                ClearThreat(state);   // a fresh monster starts with an empty threat table
            }
            _npcs[fresh] = state;
            MoveSpawnSlot(state, entityOid, fresh);

            if (corpse is not null && !LootService.CorpseOnClients(entityOid, deathStamp))
                _corpses.TryRemove(new KeyValuePair<uint, NpcRuntimeState>(entityOid, corpse));

            _logger.LogDebug("NPC {NpcId} respawned on map {Map} as entity {Entity:X8} (was {Old:X8})",
                state.NpcId, state.MapId, fresh, entityOid);
            return fresh;
        }
    }

    /// <summary>
    /// Forgets the corpse left under <paramref name="corpseHandle"/> by a respawn, once its 2507 has gone out
    /// (LootService's despawn calls it after releasing the corpse). Only the snapshot of the death stamped
    /// <paramref name="deathStamp"/> goes; with none registered (the NPC has not respawned yet, or its corpse
    /// had already left when it did) this does nothing.
    /// </summary>
    public void RetireCorpse(uint corpseHandle, DateTime? deathStamp)
    {
        if (_corpses.TryGetValue(corpseHandle, out var corpse) && corpse.DeathTime == deathStamp)
            _corpses.TryRemove(new KeyValuePair<uint, NpcRuntimeState>(corpseHandle, corpse));
    }

    /// <summary>A dead copy of <paramref name="state"/> as it died, for the handle its corpse keeps. Threat
    /// and aggro start empty (the death cleared them).</summary>
    private static NpcRuntimeState CorpseSnapshot(NpcRuntimeState state) => new()
    {
        EntityOid = state.EntityOid,
        NpcId = state.NpcId,
        MapId = state.MapId,
        Hp = 0,
        MaxHp = state.MaxHp,
        MaxSp = state.MaxSp,
        Atk = state.Atk,
        Def = state.Def,
        ExpReward = state.ExpReward,
        Camp = state.Camp,
        CorpseGatherable = state.CorpseGatherable,
        Level = state.Level,
        IsDead = true,
        DeathTime = state.DeathTime,
        Spawn = state.Spawn,
        NavCell = state.NavCell,
        PosX = state.PosX,
        PosY = state.PosY,
        NextMoveAt = state.NextMoveAt,
    };

    /// <summary>Points the spawn slot that held <paramref name="oldHandle"/> at <paramref name="newHandle"/>,
    /// so a later map load finds the respawned NPC. The slot is (map, spawn index); a state with no spawn row
    /// is looked up by its handle instead.</summary>
    private void MoveSpawnSlot(NpcRuntimeState state, uint oldHandle, uint newHandle)
    {
        if (state.Spawn is { } spawn && _handles.TryGetValue((state.MapId, spawn.Index), out var current)
            && current == oldHandle)
        {
            _handles[(state.MapId, spawn.Index)] = newHandle;
            return;
        }
        foreach (var kv in _handles)
            if (kv.Value == oldHandle)
            {
                _handles[kv.Key] = newHandle;
                return;
            }
    }

    /// <summary>
    /// Get all dead NPCs that are ready to respawn.
    /// </summary>
    public IEnumerable<NpcRuntimeState> GetRespawnReady()
    {
        var now = DateTime.UtcNow;
        return _npcs.Values
            .Where(s => s.IsDead && s.DeathTime.HasValue && now - s.DeathTime.Value >= RespawnDelay);
    }

    /// <summary>
    /// Get all alive NPCs on a map, optionally filtered by camp.
    /// </summary>
    public IEnumerable<NpcRuntimeState> GetAliveNpcs(uint mapId, int? campFilter = null)
    {
        return _npcs.Values
            .Where(s => s.MapId == mapId && !s.IsDead)
            .Where(s => campFilter == null || s.Camp == campFilter.Value);
    }

    /// <summary>
    /// Check if an entity OID belongs to an NPC we track.
    /// </summary>
    public bool IsNpcEntity(uint entityOid)
    {
        return _npcs.ContainsKey(entityOid);
    }

    /// <summary>The actor type tag every NPC handle must carry in its low byte.</summary>
    /// <remarks>
    /// The client classifies actors by the low byte of their handle: 0x11 PC, 0x13 NPC,
    /// 0x14 function object, 0x15 party. NPC handles carry 0x13 and PC handles 0x11 (as in 2506, 2311, 2497 and 2505).
    /// </remarks>
    public const byte NpcActorTag = 0x13;

    /// <summary>Bit width of the handle's type-tag byte; the counter sits above it.</summary>
    private const int HandleTagBits = 8;

    /// <summary>Mask of the handle's 24-bit counter.</summary>
    private const uint HandleCounterMask = 0xFFFFFFu;

    // Handle allocation. Sequential: a counter in the upper 24 bits and the type tag in the low byte, with no
    // relation to the spawn index. A spawn slot keeps its handle only for one life: Respawn draws
    // a fresh one from the same counter and moves the slot to it.
    private readonly ConcurrentDictionary<(uint MapId, int SpawnIndex), uint> _handles = new();
    private int _handleSeq;

    /// <summary>The next unused handle, <c>(seq &lt;&lt; 8) | 0x13</c> (see <see cref="AllocateHandle"/>).</summary>
    private uint NextHandle()
    {
        // 24 bits of counter space (16.7M) against a few tens of thousands of spawns plus one per respawn.
        // Once the counter wraps, a number still held by a live NPC or a corpse is skipped, and so is 0.
        while (true)
        {
            var seq = (uint)Interlocked.Increment(ref _handleSeq) & HandleCounterMask;
            var handle = (seq << HandleTagBits) | NpcActorTag;
            if (seq != 0 && !_npcs.ContainsKey(handle) && !_corpses.ContainsKey(handle))
                return handle;
        }
    }

    /// <summary>
    /// Allocate (or return the existing) wire handle for a spawn: the handle of the NPC the slot holds now,
    /// which is a fresh one after every respawn (<see cref="Respawn"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Format: <c>(seq &lt;&lt; 8) | 0x13</c> — a 24-bit counter plus the NPC type tag.
    /// </para>
    /// <para>
    /// This REPLACED a bit-packed <c>(mapId &lt;&lt; 16) | spawnIndex</c> scheme, which cannot work:
    /// in ZoneSpawn.json, MapId reaches 426,103 (19 bits) and Index reaches 7,373
    /// (13 bits), so map plus index already needs the full 32 bits and leaves no room for the type
    /// tag. The old scheme also masked MapId to 16 bits, silently aliasing every map above 65,535
    /// onto another map's handle space.
    /// </para>
    /// <para>
    /// Because the handle no longer encodes the map, callers must resolve a handle through the
    /// registry (<see cref="GetNpc"/>) and read <see cref="NpcRuntimeState.MapId"/> rather than
    /// shifting bits out of it.
    /// </para>
    /// </remarks>
    public uint AllocateHandle(uint mapId, int spawnIndex)
        => _handles.GetOrAdd((mapId, spawnIndex), _ => NextHandle());
}
