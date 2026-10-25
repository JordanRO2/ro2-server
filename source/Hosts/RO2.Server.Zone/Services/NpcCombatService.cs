using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RO2.Application.Interfaces;
using RO2.Domain.GameData;
using RO2.Network.Protocol;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Inventory.Domain;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Makes monsters fight back.
/// </summary>
/// <remarks>
/// <para>
/// A monster hitting a player is a large share of combat traffic: NPC-to-PC damage frames outnumber
/// PC-to-NPC ones. Without them every fight would be one-sided.
/// </para>
/// <para>
/// A PC attack (C2S 1498 -> S2C 2694, then C2S 1499 -> S2C 2696) is answered in the same tick:
/// <code>
///   S2C 2694   NPC -> PC   retaliation swing start
///   S2C 2696   NPC -> PC   damage
///   S2C 2398   subject NPC, state 3
/// </code>
/// There is NO new opcode to model: retaliation is the pair we already build with the roles
/// swapped. The one structural difference is timing: a PC attacker's 2694 and 2696 are separated by a client
/// round trip, while an NPC attacker's go out together, and this is the one place a server-side timer is
/// correct.
/// </para>
/// <para>
/// Swing cadence: the NPC swing-to-swing interval per (attacker, target) is about 2.1 s (<see cref="SwingInterval"/>).
/// </para>
/// <para>
/// A MONSTER CAN KILL YOU. A hit that takes HP to 0 runs the death sequence (the layout of each
/// message is on its builder in <see cref="PlayerDeathPackets"/>):
/// <code>
///   2694  swing start                       map
///   2581  NfyKharaFailed, one per held      victim and its party (KharaFailPackets)
///         Fail_Type-49 Khara
///   2398  victim, state 6                   map, victim included   (sent BEFORE the lethal hit)
///   2696  lethal hit, victim HP 0 and SP 0  map
///   2503  armour wear of that hit           victim
///   2391  OnDeadMe (worn-gear records)      victim
///   2836  [f32 0]   2837 [7 x 00]           victim
///   2498 [2315] 2493 2494, per ADH-0        victim / map (DeathEffects: the effects a death ends, ascending id)
///         effect
///   2398  victim, state 6 again             map, victim included
///   2630  flag 2, threat 0: the victim      the players whose open meter (1466) is that monster, one per
///         leaving that monster's table      monster whose threat table held the victim (SendAggroChangeAsync)
///   2392  OnDeadNotify, victim = the PC,    map, victim included
///         killer = the NPC, blob+16 = the lethal damage
///   2398  monster, state 1, blob+19 = 3     map, the killer and every other monster that held the
///         (leaves combat)                   victim, unless a live player is still engaged with it
/// </code>
/// The 2630 is built by <see cref="DropFromThreatTablesAsync"/> and the monsters' 2398 by
/// <see cref="ReleaseMonstersAsync"/>. The client answers the 2391 with 1679 ReqUpdateDeadPoint and, from
/// its revival UI, 1372 ReqPCRevival (RevivalHandler, which clears
/// <see cref="PlayerState.IsDead"/> and restores HP/SP). A lethal DoT tick runs this same sequence with its 2495 in
/// place of the 2696 (<see cref="RunDotDeathAsync"/>, from EffectService); a fall (FallDamageHandler) runs its own.
/// </para>
/// <para>
/// Not modelled: the 2392 goes out at once after the 2391 (so the pass never holds the tick); the death's
/// effects are removed at once, right after the 2836/2837 (DeathEffects); and a 2986 job-mastery change
/// before the first 2398 is not sent (mastery points this server does not keep). The death-penalty
/// durability loss is not modelled either, so the 2391 records carry the durability the armour-wear pass
/// just left. A leave-combat 2398 could also come later, on the monster's own tick (sometimes as state 5
/// before the state 1) and be followed by a 2318 walk-off that NpcMovementService owns; here the state 1
/// goes out at once, see <see cref="ReleaseMonstersAsync"/>.
/// </para>
/// </remarks>
public class NpcCombatService : ITickable
{
    private readonly NpcStateManager _npcState;
    private readonly PlayerStateManager _playerManager;
    private readonly BroadcastService _broadcast;
    private readonly DurabilityService _durability;
    private readonly NpcChatService _npcChat;
    private readonly PcCombatStateService _pcCombat;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<NpcCombatService> _logger;

    /// <summary>How often the retaliation pass runs, regardless of the loop's tick rate.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(400);

    /// <summary>The NPC swing-to-swing interval per (attacker, target): a median of 2123 ms.</summary>
    private static readonly TimeSpan SwingInterval = TimeSpan.FromMilliseconds(2123);

    /// <summary>How many <see cref="Interval"/>s of simulated time a delayed pass may bank before the surplus is dropped.</summary>
    private const int MaxBankedIntervals = 4;

    /// <summary>Percent scale of the defense and parry rates (a rate of 100 is "always").</summary>
    private const double PercentScale = 100.0;

    /// <summary>Cap, in percent, of the damage a player's Defense removes from a monster's hit.</summary>
    private const double MaxDefensePercent = 75.0;

    /// <summary>The penetration a monster's hit carries: none (NPCs have no Pen stat, so the defense is used as is).</summary>
    private const int NoPenetration = 0;

    /// <summary>Lowest damage a landed monster hit deals after the defense reduction.</summary>
    private const int MinHitDamage = 1;

    /// <summary>
    /// Parry (guard) rate, in percent: <c>Parry / (ParryPerPoint * Parry + ParryBase + ParryLevelWeight * (DefLv - AtkLv))</c>,
    /// capped at <see cref="MaxParryPercent"/>. DefLv is the player's level, AtkLv the monster's.
    /// </summary>
    private const double ParryPerPoint = 0.02;
    private const double ParryBase = 40.0;
    private const double ParryLevelWeight = 0.8;
    private const double MaxParryPercent = 70.0;

    /// <summary>Damage multiplier of a guarded (parried) hit; such a hit is sent as <see cref="CombatService.DamageKindGuard"/>.</summary>
    private const double GuardDamageMultiplier = 0.5;

    /// <summary>The SP a monster reports as an attacker in 2696 (monsters carry no SP).</summary>
    private const ushort MonsterCurSp = 0;

    /// <summary>2398 blob+19 of a monster leaving combat: a fixed 3, the value most leave-combat frames carry.</summary>
    private const uint LeaveCombatTrailing = 3;

    /// <summary>2630 entry flag of a player leaving a monster's threat table (always sent with threat 0).</summary>
    private const byte AggroEntryRemoved = 2;

    /// <summary>Bit width of the actor-type tag in the low byte of an actor handle; the session id sits above it.</summary>
    private const int ActorTagBits = 8;

    /// <summary>Mask of the actor-type tag byte of an actor handle.</summary>
    private const uint ActorTagMask = 0xFF;

    /// <summary>Fallback reach of a monster whose NPCInfo row carries no Attack_Range (<see cref="NpcRuntimeState.AttackReach"/>):
    /// the same melee figure as the player's.</summary>
    internal const float Reach = AutoAttackState.MaxAttackRange;

    private readonly Dictionary<uint, DateTime> _nextSwing = new();

    /// <summary>Simulated time banked since the last pass, and a guard so a still-running broadcast
    /// batch never overlaps itself on the loop thread.</summary>
    private double _accum;
    private int _running;

    /// <summary>
    /// Serializes the retaliation pass, a DoT death (<see cref="RunDotDeathAsync"/>, which EffectService's loop
    /// calls) and a damage handler's <see cref="FlushAggroChangesAsync"/> / <see cref="FlushAggroStepsAsync"/> /
    /// <see cref="PushPendingTargetChangesAsync"/>: all edit the threat tables or
    /// <see cref="_iconTarget"/>, a plain Dictionary the pass owns.
    /// </summary>
    private readonly SemaphoreSlim _passGate = new(1, 1);

    public string Name => "NpcCombat";

    /// <remarks>Registered as <c>AddSingleton&lt;NpcCombatService&gt;()</c> in Program.cs, so the container
    /// builds it from this constructor; <see cref="IServiceScopeFactory"/> is always registered by the host.
    /// It is needed because the pass runs outside any request scope and the 2391 reads the victim's worn gear
    /// through the scoped <see cref="IInventoryRepository"/> — the same pattern DurabilityService uses.
    /// <see cref="NpcChatService"/> is the <c>AddSingleton&lt;NpcChatService&gt;()</c> registration in
    /// Program.cs; it depends on nothing that depends on this service, so there is no cycle.</remarks>
    public NpcCombatService(NpcStateManager npcState, PlayerStateManager playerManager,
        BroadcastService broadcast, DurabilityService durability, NpcChatService npcChat,
        PcCombatStateService pcCombat, IServiceScopeFactory scopeFactory, ILogger<NpcCombatService> logger)
    {
        _npcState = npcState;
        _playerManager = playerManager;
        _broadcast = broadcast;
        _durability = durability;
        _npcChat = npcChat;
        _pcCombat = pcCombat;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>Driven by the central game loop. Banks simulated time and runs the retaliation pass
    /// every <see cref="Interval"/>, independent of the loop's current Hz. The pass itself broadcasts
    /// asynchronously, so it is fired off the loop thread (fire-and-forget) with a re-entrancy guard.</summary>
    public void Tick(double deltaSeconds)
    {
        _accum += deltaSeconds;
        double interval = Interval.TotalSeconds;
        if (_accum < interval) return;

        // A previous pass is still flushing to sockets — keep the banked time (capped) and retry next tick.
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            if (_accum > interval * MaxBankedIntervals) _accum = interval * MaxBankedIntervals;
            return;
        }
        _accum = 0;
        _ = RunTickAsync();
    }

    private async Task RunTickAsync()
    {
        try
        {
            await _passGate.WaitAsync();
            try
            {
                await TickAsync(CancellationToken.None);
            }
            finally
            {
                _passGate.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NPC combat tick failed");
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var players = _playerManager.GetAllPlayers().ToArray();

        // Threat is banked where damage lands (NpcStateManager.ApplyDamage with the attacker's handle),
        // never here. A damage path that calls ApplyDamage without the attacker banks
        // nothing, so a monster hurt only through such paths has an empty table: no 2630, an empty
        // 2629 and no target-of-target in its 2643.

        // (1) + (2): whatever the damage handlers have not flushed themselves (FlushAggroChangesAsync) — the DoT
        // 2495 path (EffectService.TickNpcEffectAsync), and any hit path that does not call the flush.
        if (!await FlushAggroCoreAsync(ct))
            return;

        // Respawns take fresh handles, so a swing timer is dropped with its monster's life (as _iconTarget is),
        // or the map would gain one entry per NPC life and never lose one.
        foreach (var handle in _nextSwing.Keys.ToArray())
            if (_npcState.GetNpc(handle) is not { IsDead: false })
                _nextSwing.Remove(handle);

        // (3) RETALIATION + AUTONOMOUS AGGRO. The pass now iterates the MONSTERS, not the engaged players, so a
        // monster acts on its own threat target even when nobody is targeting it: it swings at whoever it is
        // aggroed on, an AGGRESSIVE monster acquires a nearby player who has not hit it (NpcAggression, custom),
        // the target drops to the next-highest attacker when the top one dies or leaves the map (the user's
        // "switch to the second"), and a monster with nobody left resets (clears threat, heals to full; the chat
        // service's sweep then sends its leave-combat 2398). In the single-attacker fight the monster's only
        // threat row is that player, so the SWING loop below is unchanged — only this driver and the
        // acquisition / re-selection / reset around it are new.
        var engagedBy = new Dictionary<uint, PlayerState>();
        foreach (var p in players)
            if (p.IsInCombat && p.TargetEntityId != 0 && !p.IsDead)
                engagedBy.TryAdd(p.TargetEntityId, p);

        // Decide, per monster, whether it acts this tick and at whose threat it aims. Acquisition and the reset
        // happen here; the swing itself is the single loop below, so its body keeps its shape.
        var acting = new List<(NpcRuntimeState Npc, PlayerState? Iterating, PlayerState? Target)>();
        var retargeted = new HashSet<uint>();
        foreach (var mapId in players.Select(p => p.MapId).Distinct())
        {
            var onMap = _playerManager.GetPlayersOnMap(mapId).Where(p => !p.IsDead).ToArray();
            foreach (var monster in _npcState.GetAliveNpcs(mapId).ToArray())
            {
                if (monster.Camp <= NpcSpawnService.CampFriendly)
                    continue;
                engagedBy.TryGetValue(monster.EntityOid, out var iterating);

                // (3a) ACQUISITION: an aggressive, not-yet-fighting monster takes the nearest player in its sight
                // that its mode allows. One point of threat seats the target, so the 2630 meter and the
                // enter-combat 2398 flow exactly as a first hit's would.
                if (monster.AggroTargetHandle == 0 && iterating is null)
                {
                    if (monster.Aggression == NpcAggressionMode.None)
                        continue;
                    var acquired = AcquireNearest(monster, onMap);
                    if (acquired is null)
                        continue;
                    _npcState.AddThreat(monster.EntityOid, acquired.EntityId, acquired.Name, AcquireThreat);
                    await _npcChat.EnterCombatAsync(monster, acquired.EntityId);
                    retargeted.Add(monster.EntityOid);
                }

                // (3b) TARGET VALIDATION: a top-threat target that has died or left the map is replaced by the
                // next-highest attacker that is still a live player here.
                var target = ResolvePlayer(monster.AggroTargetHandle);
                if (monster.AggroTargetHandle != 0
                    && (target is null || target.IsDead || target.MapId != monster.MapId))
                {
                    target = ResolvePlayer(ReselectLiveTarget(monster));
                    retargeted.Add(monster.EntityOid);
                }

                // (3c) RESET: no live target and no engaged player — the monster leaves the fight, clearing its
                // threat and healing to full. The chat service's sweep sends its leave-combat 2398 (state 5 then
                // 1) once the target is 0 and nobody is engaged with it.
                if (target is null && iterating is null)
                {
                    // Clear + heal, and push the target change so a watcher's 2643 panel does not keep showing the
                    // monster aimed at the attacker that just left (the death path does the same via its 2630/2643).
                    if (_npcState.LeashReset(monster.EntityOid))
                    {
                        retargeted.Add(monster.EntityOid);
                        // Show the healed HP now (NPC 2401): the floating bar is otherwise stale until the next hit.
                        await ActorVitalsBroadcast.SendNpcAutoHealAsync(_broadcast, monster);
                    }
                    _nextSwing.Remove(monster.EntityOid);
                    continue;
                }

                acting.Add((monster, iterating, target));
            }
        }

        if (retargeted.Count > 0)
            await PushTargetChangesAsync(retargeted, ct);

        // (4) SWING: one monster at a time, at its top-threat target if in reach, otherwise the engaged player —
        // the same choice SelectVictim made before. Never swing when nobody is in reach, and do NOT consume the
        // swing timer in that case.
        foreach (var (npc, iterating, target) in acting)
        {
            var victim = SelectVictim(npc, iterating ?? target);
            if (victim is null)
                continue;

            if (_nextSwing.TryGetValue(npc.EntityOid, out var next) && next > now)
                continue;
            _nextSwing[npc.EntityOid] = now + SwingInterval;

            // Mitigate with the REAL RO2 (AoV) DefenseRate against the player's EFFECTIVE Defense (2498
            // trait 26 = VitDefenseValue·VIT + worn armour), attacker level = the monster's:
            //   Defense% = 100·Def / (Def + 870 + AttackerLv·35), capped 75%.
            // This replaces the earlier invented curve that used the player's RAW VIT and ignored armour.
            int def = Math.Max(0, victim.CombatDefense);
            double defEff = StatService.PenetratedDef(def, NoPenetration, Math.Max(0, npc.Level));  // NPCs carry no Pen (no-op)
            double defPct = Math.Min(MaxDefensePercent, PercentScale * defEff / (defEff + StatService.DefenseRateAdd + Math.Max(0, npc.Level) * StatService.DefenseRateMulti));
            var damage = Math.Max(MinHitDamage, (int)Math.Round(npc.Atk * (1.0 - defPct / PercentScale)));

            // A PC whose active effects add Miss_Rate +100% (trait 35, Trait_Kind 4: the map-move buff 60205 that
            // every 1304 reply grants for 10 s, EffectService.ApplyMapMoveBuffAsync) takes no hit: the swing goes
            // out as a kind-2 miss, damage 0, HP unchanged, and with no armour wear below (a miss wears none).
            // Without this the client would show an "invincible" icon over a PC the server still hurts. A rate below
            // 100% is not rolled here: the evasion formula is not established, only the certain case is.
            bool evaded = MissRateOf(victim) >= EffectService.FullMissRate;

            // PARRY — the player's Parry stat (trait 61) can GUARD an incoming hit down to ×0.5, flagged as
            // damageKind 3. Rate = Parry/(0.02·Parry + 40 + 0.8·(DefLv−AtkLv)) cap 70% (AoV); here
            // DefLv = the player, AtkLv = the monster. NPCs carry no Crit stat, so they never crit the player.
            byte damageKind = CombatPackets.DamageKindNormal;
            int parry = victim.CombatParry;
            if (evaded)
            {
                damage = 0;
                damageKind = CombatPackets.DamageKindMiss;
            }
            else if (parry > 0)
            {
                double parryPct = Math.Min(MaxParryPercent, parry / (ParryPerPoint * parry + ParryBase + ParryLevelWeight * (victim.Level - Math.Max(0, npc.Level))));
                if (parryPct > 0 && Random.Shared.NextDouble() * PercentScale < parryPct)
                {
                    damage = Math.Max(MinHitDamage, (int)Math.Round(damage * GuardDamageMultiplier));
                    damageKind = CombatService.DamageKindGuard;
                }
            }
            // A player in a started field duel is not killed by a monster's hit: it is left at HP 1 and the duel ends as
            // fought out, the other duelist the winner. FieldPvPCombat.TryFightOutMonsterHitAsync then sends this
            // hit's 2694, the end, the 2696 and the armour wear itself; false leaves the hit to the code below.
            if (victim.Hp - damage <= 0)
            {
                using var duelScope = _scopeFactory.CreateScope();
                if (await FieldPvPCombat.TryFightOutMonsterHitAsync(npc, victim, damage, damageKind, _playerManager,
                        duelScope.ServiceProvider, _broadcast, ct))
                    continue;
            }

            // A hit that reaches 0 kills. The dead state is set BEFORE anything is sent, so no other pass or
            // handler sees a 0-HP player as alive: this loop, the threat pass and SelectVictim skip IsDead, and
            // RegenService skips Hp <= 0. SP goes to 0 with it (the lethal 2696 and the 2391 head+85 report SP 0), and
            // RevivalHandler restores
            // both. The engagement is dropped too: a corpse is not attacking, and a stale TargetEntityId
            // would let this monster reach the player again after a save-point revival (SelectVictim now also
            // compares maps for the iterating player).
            // Decided and written as one step under the victim's vitals lock (ApplyHpDelta/SetVitalsDead), so a
            // concurrent spend/heal on the session thread cannot be lost under this write.
            bool lethal;
            lock (victim.Vitals)
            {
                lethal = victim.Hp - damage <= 0;
                victim.ApplyHpDelta(-damage);
                if (lethal)
                    victim.SetVitalsDead();
            }
            if (lethal)
                AutoAttackState.Disengage(victim);
            else
                _pcCombat?.MarkCombat(victim);   // taking a monster hit enters/keeps the player's battle stance

            // 2694 then 2696 in the SAME tick, roles swapped.
            await _broadcast.BroadcastToMapAsync(victim.MapId, ZoneS2COpcodes.NfyNormalAttackStart,
                w => w.WriteBytes(CombatPackets.BuildAttackStart2694(npc.EntityOid, victim.EntityId)));

            // The victim's state-6 2398 goes out BEFORE the lethal 2696. Map broadcast with no exclusion: the victim needs
            // it, and other PCs receive it too.
            if (lethal)
                await SendDeathHeadAsync(victim, ct);

            await _broadcast.BroadcastToMapAsync(victim.MapId, ZoneS2COpcodes.NfyAttackDamage,
                w => w.WriteBytes(CombatPackets.BuildAttackDamage2696(
                    attackerHandle: npc.EntityOid,
                    attackerCurHp: (uint)Math.Max(0, npc.Hp),
                    attackerCurSp: MonsterCurSp,
                    victimHandle: victim.EntityId,
                    victimCurHpAfter: (uint)Math.Max(0, victim.Hp),
                    victimCurSp: (ushort)Math.Clamp(victim.Mp, 0, ushort.MaxValue),
                    damage: (uint)damage,
                    damageKind: damageKind)));   // 1 normal / 3 guard (x0.5) when the player parries

            // Armour wear on the hit just TAKEN: one 2503 per worn armour piece, unicast to the victim,
            // right after its 2696. Every hit this pass sends does damage (kind 1 normal or 3 guard) except
            // the evaded one, and those are exactly the hits that wear armour; a miss wears none.
            // See DurabilityService.ArmourWearStep for the per-piece step. Awaited, not fired off, so the burst
            // cannot overtake the 2696 it belongs to. The one miss this pass sends (an evaded hit, above) wears nothing.
            if (!evaded)
                await _durability.ApplyArmourWearOnTakenHitAsync(victim, ct);

            // A 2696 that names the PC, while its Illusion Pyramid is armed, is followed by a 29005 close after the
            // hit's 2503s (IllusionPyramidService, COMBAT). Not on the lethal hit: the death close above already ended the use.
            if (!lethal && _broadcast.GetSession(victim.SessionId) is { IsConnected: true } hitSession)
                await IllusionPyramidService.NotifyCombatAsync(hitSession, victim, _logger, ct);

            // After the lethal hit's own 2503 burst (2696, the 2503s, then 2391), so the 2391 records carry the
            // durability that burst just persisted.
            if (lethal)
                await SendPlayerDeathAsync(victim, npc.EntityOid, damage, ct);

            if (ct.IsCancellationRequested)
                return;
        }
    }

    /// <summary>
    /// The head of a player's death, before the lethal frame: everything up to and including the first state-6 2398.
    /// </summary>
    /// <remarks>
    /// Order for a monster or DoT kill: [2909 29012] -> 2938 -> state-0 2398 -> death-wear 2503s -> [Khara] ->
    /// 2581s -> s6. This path sends no 2938 / s0, so the rest keeps that order without them:
    /// 1. the Illusion Pyramid close 29012 when the pyramid is armed (IllusionPyramidService, DEATH);
    /// 2. the death-wear burst, slots 1..12, persisted before the 2391 reads the gear (DurabilityService.ApplyDeathWearAsync);
    /// 3. the Complete_Type 26 death count of the victim's Kharas (KharaService.OnDeathAsync: its 2574/2577 go
    ///    between the burst and the s6);
    /// 4. the Khara failures, right before the first state-6 2398 (see KharaFailPackets).
    /// The scope reaches the repositories and singletons from this pass, which runs outside any request.
    /// </remarks>
    private async Task SendDeathHeadAsync(PlayerState victim, CancellationToken ct)
    {
        using (var scope = _scopeFactory.CreateScope())
        {
            var victimSession = _broadcast.GetSession(victim.SessionId);
            if (victimSession is not null && victimSession.IsConnected)
                await IllusionPyramidService.CloseOnDeathAsync(victimSession, victim, scope.ServiceProvider, _logger, ct);
            await _durability.ApplyDeathWearAsync(victim, ct);
            if (victimSession is not null && victimSession.IsConnected)
                await KharaService.OnDeathAsync(victimSession, victim, scope.ServiceProvider, _logger, ct);
            await KharaFailPackets.SendDeathFailuresAsync(victim, scope.ServiceProvider, _broadcast, _logger, ct);
        }

        var deadState = PlayerDeathPackets.BuildOnChangeStateDead2398(victim.EntityId);
        await _broadcast.BroadcastToMapAsync(victim.MapId, ZoneS2COpcodes.OnChangeState,
            w => w.WriteBytes(deadState));
    }

    /// <summary>
    /// A PLAYER killed by a DoT tick: the monster-kill sequence with the lethal 2495 in place of the 2696. The caller
    /// (EffectService's tick loop) has already set the dead state (HP 0, SP 0, IsDead, disengaged). Sent here: the
    /// death head (<see cref="SendDeathHeadAsync"/>, ending in the state-6 2398), then <paramref name="sendLethalTick"/>
    /// (the caller's 2495 with victim HP 0 / SP 0, to the map), then <see cref="SendPlayerDeathAsync"/> with the DoT's
    /// caster as the 2392 killer and the tick's damage at its blob+16.
    /// </summary>
    /// <remarks>
    /// Same order as a monster kill, with the lethal 2495 between the state-6 2398 and the 2391, and the 2392
    /// naming the caster (a 0x13 monster or a 0x11 PC, the victim itself included) with the tick's damage.
    /// Runs under the pass's gate
    /// because it edits the threat tables and the icon-target cache the pass edits.
    /// </remarks>
    public async Task RunDotDeathAsync(PlayerState victim, uint casterHandle, int lethalDamage,
        Func<Task> sendLethalTick, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(victim);
        ArgumentNullException.ThrowIfNull(sendLethalTick);
        await _passGate.WaitAsync(ct);
        try
        {
            await SendDeathHeadAsync(victim, ct);
            await sendLethalTick();
            await SendPlayerDeathAsync(victim, casterHandle, lethalDamage, ct);
        }
        finally
        {
            _passGate.Release();
        }
    }

    /// <summary>
    /// Sends now the 2630 NfyAggroChanged steps queued since the last flush, and BEFORE them the 2643 target changes they
    /// cause, instead of leaving both to the next 400 ms pass: [2643, 2630, damage frame]. The 2696 handlers (1499
    /// NormalAttackDamageHandler, ArcherAttackHandler) and 1509 SkillDamageHandler (2718) call it after their ApplyDamage
    /// loop and right before broadcasting their damage frame. 1549 SkillDamageTickHandler (2797) puts the 2643 after its
    /// frame instead, with <see cref="FlushAggroStepsAsync"/> + <see cref="PushPendingTargetChangesAsync"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The 2630 step goes out RIGHT BEFORE the damage frame it belongs to (2696, 2718 or 2797). On the pass's
    /// flush it comes after the damage frame. A KILLING hit carries no step before it; the
    /// flush already drops the steps of a dead monster, so a kill sends none here either.
    /// </para>
    /// <para>
    /// The 2643 target-change push is never placed between the steps and the damage frame (S T D, with S a 2630
    /// of that monster, T the 2643 and D the damage frame, would give every watcher that has both the meter and
    /// the monster selected a target change before the hit lands). Where the 2643 sits relative to the damage
    /// frame depends on the damage type; the paths that send it after the frame use
    /// <see cref="FlushAggroStepsAsync"/> + <see cref="PushPendingTargetChangesAsync"/>. These are pushes, not
    /// answers to a 1307.
    /// </para>
    /// <para>
    /// The queue is global, so one handler's flush can also send another hit's pending step, which is harmless: threat
    /// only grows and each 2630 carries the total. Runs under <see cref="_passGate"/>, because <see cref="_iconTarget"/>
    /// is a plain Dictionary the pass owns; the pass itself calls the unlocked core. The pass keeps draining, for the paths
    /// that do not flush (the DoT 2495 ticks, EffectService.TickNpcEffectAsync).
    /// </para>
    /// </remarks>
    public async Task FlushAggroChangesAsync(CancellationToken ct = default)
    {
        await _passGate.WaitAsync(ct);
        try
        {
            var (steps, changed) = DrainAggroChanges();
            await PushTargetChangesAsync(changed, ct);
            if (!ct.IsCancellationRequested)
                await SendAggroStepsAsync(steps, ct);
        }
        finally
        {
            _passGate.Release();
        }
    }

    /// <summary>
    /// Sends now only the 2630 steps queued since the last flush, and keeps the monsters whose target they may have moved
    /// for <see cref="PushPendingTargetChangesAsync"/>, which the caller sends after its damage frame. 1549
    /// SkillDamageTickHandler calls the pair around its 2797: the target-change 2643 goes after the 2797 (see
    /// <see cref="FlushAggroChangesAsync"/>).
    /// </summary>
    public async Task FlushAggroStepsAsync(CancellationToken ct = default)
    {
        await _passGate.WaitAsync(ct);
        try
        {
            var (steps, changed) = DrainAggroChanges();
            _pendingTargetPush.UnionWith(changed);
            await SendAggroStepsAsync(steps, ct);
        }
        finally
        {
            _passGate.Release();
        }
    }

    /// <summary>
    /// Pushes the 2643 target changes <see cref="FlushAggroStepsAsync"/> left pending (to whoever has each monster
    /// selected, only where the target moved). Sent after the damage frame; a monster that died in the meantime is skipped
    /// (its kill site sends the HP-0 panel). Anything still pending goes out on the next pass.
    /// </summary>
    public async Task PushPendingTargetChangesAsync(CancellationToken ct = default)
    {
        await _passGate.WaitAsync(ct);
        try
        {
            var pending = _pendingTargetPush.ToArray();
            _pendingTargetPush.Clear();
            await PushTargetChangesAsync(pending, ct);
        }
        finally
        {
            _passGate.Release();
        }
    }

    /// <summary>Monsters whose 2643 push <see cref="FlushAggroStepsAsync"/> deferred to after the caller's damage frame.
    /// Under <see cref="_passGate"/>, as <see cref="_iconTarget"/>.</summary>
    private readonly HashSet<uint> _pendingTargetPush = new();

    /// <summary>
    /// Takes every queued threat bank off the NpcStateManager queue: the 2630 bodies of the monsters still alive, and
    /// the set of those monsters. A step whose monster has died (or whose handle is gone) is dropped (see
    /// <see cref="FlushAggroCoreAsync"/>).
    /// </summary>
    private (List<(uint NpcHandle, uint MapId, byte[] Body)> Steps, HashSet<uint> Changed) DrainAggroChanges()
    {
        var steps = new List<(uint, uint, byte[])>();
        var changed = new HashSet<uint>();
        while (_npcState.TryDequeueAggroChange(out var ch))
        {
            if (_npcState.GetNpc(ch.NpcHandle) is not { IsDead: false })
                continue;
            steps.Add((ch.NpcHandle, ch.MapId, AggroPackets.BuildAggroChanged2630(
                ch.NpcHandle, ch.CurrentTarget, ch.PcHandle, ch.PcName, ch.Threat)));
            changed.Add(ch.NpcHandle);
        }
        return (steps, changed);
    }

    /// <summary>Sends each drained 2630 to its monster's meter audience (<see cref="SendAggroChangeAsync"/>), in queue
    /// order. Returns false when <paramref name="ct"/> stopped it.</summary>
    private async Task<bool> SendAggroStepsAsync(List<(uint NpcHandle, uint MapId, byte[] Body)> steps, CancellationToken ct)
    {
        foreach (var (npcHandle, mapId, body) in steps)
        {
            await SendAggroChangeAsync(npcHandle, mapId, body);
            if (ct.IsCancellationRequested)
                return false;
        }
        return true;
    }

    /// <summary>
    /// The pass's flush, without the gate: (1) one 2630 per queued threat bank, (2) the 2643 of each monster whose target
    /// moved, including those a <see cref="FlushAggroStepsAsync"/> left pending. Returns false when <paramref name="ct"/>
    /// stopped it. With no damage frame to place against, the pass keeps its old order (steps, then pushes).
    /// </summary>
    /// <remarks>
    /// (1) One 2630 per threat bank since the last flush (every hit with an attacker, NpcStateManager.AddThreatCore), so an
    /// open aggro meter tracks the per-hit threat step. Who receives it is
    /// SendAggroChangeAsync's rule: the players whose open meter (their latest 1466 ReqOpenAggroList) is that monster, not
    /// the map and not the attacker. BARE body (NONE-family, raw): no compact envelope, sent on the internal UNENC path
    /// exactly like 2318 OnMoveNPC. A step whose monster has died (or whose handle is gone: a respawn takes a fresh
    /// handle, so the lookup misses) is dropped: no flag-1 2630 follows a kill, and the killing hit itself carries no step (see
    /// <see cref="FlushAggroChangesAsync"/>). (2) The same changes, as the target panel of whoever has the monster selected.
    /// </remarks>
    private async Task<bool> FlushAggroCoreAsync(CancellationToken ct)
    {
        var (steps, changed) = DrainAggroChanges();
        changed.UnionWith(_pendingTargetPush);
        _pendingTargetPush.Clear();
        if (!await SendAggroStepsAsync(steps, ct))
            return false;

        await PushTargetChangesAsync(changed, ct);
        return !ct.IsCancellationRequested;
    }

    /// <summary>
    /// The part of a player's death that follows the lethal hit: 2391 + 2836 + 2837 to the victim, the removal of
    /// its After_Death_Handling 0 effects (<see cref="DeathEffects"/>), then the state-6 2398 again, the victim's 2630
    /// threat-table removals, the 2392 OnDeadNotify and the monsters' leave-combat 2398s to the whole map (see the
    /// sequence in the class remarks).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The 2391 must reach the victim even when the worn-gear read fails, because it is what opens the
    /// client's death UI and so the only way to 1372 ReqPCRevival; <see cref="PlayerDeathPackets.SendDeadMeAsync"/>
    /// then sends it with an empty record vector and returns false, which is logged here.
    /// </para>
    /// <para>
    /// 2392 blob+16 is the LETHAL HIT'S DAMAGE for a PC victim — the killing blow, not a later tick: a DoT tick
    /// that lands on the corpse (HP 0) before the 2392 does not replace it. The same holds for monster victims
    /// (LootService sends 0 there). The client never reads the slot (ActorVitalsPackets.BuildDeadNotify2392), so
    /// this is fidelity, not a behaviour change. blob+12 is the killer: an NPC (0x13), a PC (0x11) or none.
    /// </para>
    /// </remarks>
    private async Task SendPlayerDeathAsync(PlayerState victim, uint killerHandle, int lethalDamage,
        CancellationToken ct)
    {
        var session = _broadcast.GetSession(victim.SessionId);
        if (session is not null && session.IsConnected)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var inventory = scope.ServiceProvider.GetRequiredService<IInventoryRepository>();
                var gameData = scope.ServiceProvider.GetRequiredService<GameDataStore>();
                if (!await PlayerDeathPackets.SendDeadMeAsync(session, victim, inventory, gameData, ct))
                    _logger.LogWarning("{Name} died but the worn-gear read failed; 2391 sent with no records",
                        victim.Name);

                // The death ends the victim's After_Death_Handling 0 effects, right after the 2391 + 2836 + 2837 and
                // before the second state-6 2398 and the 2392 (DeathEffects: the removals are ADH 0 effects, in ascending
                // id). Each removal is EffectService's
                // 2498 -> [2315] -> 2493 -> 2494, which takes no lock of this pass, so it is safe under _passGate.
                try
                {
                    await DeathEffects.DropDeathEffectsAsync(session, victim, gameData, scope.ServiceProvider);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Session {Id}: removing the effects a death ends failed", victim.SessionId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Session {Id}: sending 2391 OnDeadMe failed", victim.SessionId);
            }
        }

        var deadState = PlayerDeathPackets.BuildOnChangeStateDead2398(victim.EntityId);
        await _broadcast.BroadcastToMapAsync(victim.MapId, ZoneS2COpcodes.OnChangeState,
            w => w.WriteBytes(deadState));

        // The victim leaves the threat tables BEFORE the 2392, and the monsters leave combat AFTER it
        // (see ReleaseMonstersAsync).
        var released = await DropFromThreatTablesAsync(victim, killerHandle);

        var deadNotify = ActorVitalsPackets.BuildDeadNotify2392(victim.EntityId, killerHandle,
            (uint)Math.Max(0, lethalDamage));
        await _broadcast.BroadcastToMapAsync(victim.MapId, ZoneS2COpcodes.OnDeadNotify,
            w => w.WriteBytes(deadNotify));

        await ReleaseMonstersAsync(released);

        _logger.LogInformation("{Name} was killed by {Killer:X8} ({Damage} damage) on map {Map}",
            victim.Name, killerHandle, lethalDamage, victim.MapId);
    }

    /// <summary>
    /// The monster whose threat meter each player last opened: the handle of its latest 1466 ReqOpenAggroList that
    /// named a monster this server knows. Keyed by the PlayerState itself, so a gone player's entry goes with it and
    /// a reused session id never inherits a stale subscription.
    /// </summary>
    private readonly ConditionalWeakTable<PlayerState, StrongBox<uint>> _aggroMeter = new();

    /// <summary>
    /// Records that <paramref name="player"/> opened the threat meter of <paramref name="npcHandle"/>, which from now
    /// on decides who receives that monster's 2630s (<see cref="AggroRecipients"/>). OpenAggroListHandler calls it
    /// for a 1466 that names a known monster, the same requests it answers with a 2629.
    /// </summary>
    /// <remarks>
    /// A handle that is not monster-tagged (low byte 0x13) changes nothing: a PC-tagged 1466 is answered with
    /// nothing (see OpenAggroListHandler), and the request does not cancel the meter either. A 1466 always carries
    /// body+4 == 1, so there is no close request to model. Opening another monster's meter replaces the subscription.
    /// </remarks>
    public void WatchAggroMeter(PlayerState player, uint npcHandle)
    {
        ArgumentNullException.ThrowIfNull(player);
        if ((npcHandle & ActorTagMask) != NpcStateManager.NpcActorTag)
            return;
        _aggroMeter.AddOrUpdate(player, new StrongBox<uint>(npcHandle));
    }

    /// <summary>The monster whose meter <paramref name="player"/> last opened, 0 for none.</summary>
    internal uint AggroMeterOf(PlayerState player) =>
        _aggroMeter.TryGetValue(player, out var box) ? box.Value : 0;

    /// <summary>
    /// Sends one 2630 NfyAggroChanged <paramref name="body"/> about monster <paramref name="npcHandle"/> to the players
    /// on <paramref name="mapId"/> whose open threat meter is that monster (<see cref="AggroRecipients"/>). Raw
    /// NONE-family body on the internal UNENC path, as before.
    /// </summary>
    private async Task SendAggroChangeAsync(uint npcHandle, uint mapId, byte[] body)
    {
        foreach (var recipient in AggroRecipients(npcHandle, mapId, _playerManager.GetPlayersOnMap(mapId), AggroMeterOf))
        {
            if (_broadcast.GetSession(recipient.SessionId) is { IsConnected: true } session)
                await session.SendInternalRmiAsync(ZoneS2COpcodes.NfyAggroChanged, body);
        }
    }

    /// <summary>
    /// The 2630 audience: every player of <paramref name="onMap"/> on <paramref name="mapId"/> whose latest opened
    /// meter (<paramref name="meterOf"/>, recorded by <see cref="WatchAggroMeter"/>) is <paramref name="npcHandle"/>.
    /// The PC the entry names and its party are not told unless they opened that meter.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A 2630 goes to the clients that subscribed with 1466 ReqOpenAggroList: the receiving session had sent a 1466
    /// naming that monster, and its LATEST monster-tagged 1466 names exactly that monster (a step still in flight
    /// when the meter switched may reach the previous one).
    /// </para>
    /// <para>
    /// The attacker and its party are not the audience: a session whose own PC hits a monster whose meter it never
    /// opened gets no 2630 for that hit, and neither does a party member that never opened it. An onlooker outside
    /// the party that has the meter open does receive it.
    /// </para>
    /// <para>
    /// The map bound is a server rule. A meter names a monster of the map the client is on, and nothing here
    /// clears a subscription on a map change, so the bound is what keeps one left behind silent.
    /// </para>
    /// </remarks>
    internal static List<PlayerState> AggroRecipients(uint npcHandle, uint mapId, IEnumerable<PlayerState> onMap,
        Func<PlayerState, uint> meterOf)
    {
        var recipients = new List<PlayerState>();
        if (npcHandle == 0)
            return recipients;
        foreach (var p in onMap)
            if (p.MapId == mapId && meterOf(p) == npcHandle)
                recipients.Add(p);
        return recipients;
    }

    /// <summary>
    /// Removes a player who just died from the threat table of every live monster on their map, sends the
    /// removal as a 2630 for each, and returns the monsters that are now out of the fight: the killer and
    /// every monster that held the victim, minus those a live player is still engaged with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A 2630 entry with flag 2 is a player leaving a monster's table, always with threat 0. body+4 of a flag-2
    /// entry is the monster's target after the removal, which is what is re-selected here. The name is still
    /// sent: the body length is 22 + 2·nameChars. The removal goes at or before the 2392 naming that player as
    /// the victim, so this goes before the 2392, shortly after the 2391.
    /// </para>
    /// <para>
    /// Without the removal the corpse stays the monster's top-threat target. ReqActorDetailInfo reports it
    /// as target of target, the 2629 list still shows it, and NpcChatService's idle gate
    /// (<c>AggroTargetHandle != 0</c>) keeps a monster that survived the fight silent until it next dies.
    /// When the victim was the monster's target, the highest remaining row becomes the target (0 when the
    /// table is empty), under the same <see cref="NpcRuntimeState.ThreatLock"/>. When it was not, the
    /// target stays, because a monster keeps a target that is still on its table (see the comment in
    /// the method).
    /// </para>
    /// <para>
    /// "Engaged" uses the same test as NpcChatService's engagement sweep: a live player in combat whose
    /// target is this monster. The victim was disengaged when the lethal hit landed, so it never counts.
    /// A killer that still has another engaged player keeps fighting; otherwise it leaves combat.
    /// </para>
    /// </remarks>
    private async Task<List<NpcRuntimeState>> DropFromThreatTablesAsync(PlayerState victim,
        uint killerHandle)
    {
        var engaged = new HashSet<uint>();
        foreach (var p in _playerManager.GetAllPlayers())
            if (p.IsInCombat && p.TargetEntityId != 0 && !p.IsDead)
                engaged.Add(p.TargetEntityId);

        var released = new List<NpcRuntimeState>();
        foreach (var npc in _npcState.GetAliveNpcs(victim.MapId).ToArray())
        {
            if (npc.Camp <= NpcSpawnService.CampFriendly)
                continue;

            bool held;
            uint target;
            lock (npc.ThreatLock)
            {
                held = npc.Threat.Remove(victim.EntityId);
                // Only losing the TARGET re-selects. A monster keeps its current target while that row
                // is on the table (it moves only when another row passes it by more than 10%). So removing someone else must not
                // move the target. (NpcStateManager.AddThreatCore does not apply that 10% margin: it
                // makes the top row the target on every add, so the target is the top row and removing
                // another row leaves it the top row anyway.)
                if (held && npc.AggroTargetHandle == victim.EntityId)
                {
                    uint best = 0;
                    long bestThreat = long.MinValue;
                    foreach (var kv in npc.Threat)
                    {
                        if (kv.Value.Threat > bestThreat)
                        {
                            bestThreat = kv.Value.Threat;
                            best = kv.Key;
                        }
                    }
                    npc.AggroTargetHandle = best;
                }
                target = npc.AggroTargetHandle;
            }

            if (held)
            {
                // To the players whose open meter is this monster (SendAggroChangeAsync), as every 2630.
                await SendAggroChangeAsync(npc.EntityOid, npc.MapId, AggroPackets.BuildAggroChanged2630(
                    npc.EntityOid, target, victim.EntityId, victim.Name, 0, changeFlag: AggroEntryRemoved));
                // The re-selected target reaches whoever has this monster selected, as any other
                // target change does.
                await PushTargetChangesAsync(new[] { npc.EntityOid }, CancellationToken.None);
            }

            if ((held || npc.EntityOid == killerHandle) && !engaged.Contains(npc.EntityOid))
                released.Add(npc);
        }
        return released;
    }

    /// <summary>
    /// Takes the monsters <see cref="DropFromThreatTablesAsync"/> returned out of combat: a 2398 with state 1,
    /// blob+19 = 3 and no target to the map for each, and the end of its NpcChatService engagement.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The monsters leave combat after the 2392. The killer's last 2398 is state 1 with blob+19 = 3 and target 0.
    /// Whenever a state 5 comes first, a later frame is a state 1. So state 1 is the leave-combat
    /// state, and it is the only one sent here. blob+19 is 3 in most frames and 1001/1006/1007
    /// in the rest. That looks like a per-monster value that no table here has, so 3 is sent, as at the
    /// monster-death site in NormalAttackDamageHandler.
    /// </para>
    /// <para>
    /// Without this, the state-3 2398 the monster got when its engagement started (NpcChatService.
    /// EnterCombatAsync, from NormalAttackDamageHandler) leaves the killer marked as fighting on every
    /// client on the map. The handler's own death branch sends the same frame for the same reason. The
    /// engagement is closed here too, so the next hit on the monster sends a new state 3 with its combat line.
    /// NpcChatService's sweep would not close it: the monster may still hold other players' threat.
    /// </para>
    /// </remarks>
    private async Task ReleaseMonstersAsync(List<NpcRuntimeState> released)
    {
        foreach (var npc in released)
        {
            await _broadcast.BroadcastToMapAsync(npc.MapId, ZoneS2COpcodes.OnChangeState,
                w => w.WriteBytes(CombatPackets.BuildChangeState2398(
                    npc.EntityOid, CombatPackets.ActorState.NpcLeaveCombat, trailing: LeaveCombatTrailing)));
            _npcChat.OnNpcLeftCombat(npc.EntityOid);
        }
    }

    /// <summary>
    /// The monster's current target as its watchers were last told it, by NPC handle. Read and written
    /// only under <see cref="_passGate"/> (the pass, a DoT death and the damage handlers'
    /// flushes), so no lock.
    /// </summary>
    private readonly Dictionary<uint, uint> _iconTarget = new();

    /// <summary>
    /// Pushes a 2643 AnsActorIconInfo of each monster in <paramref name="npcHandles"/> whose current
    /// target differs from what its watchers last saw, to every player who has it selected.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The unsolicited 2643 is driven by the actor's target changing: it moves body+17 to a new target (a 2643
    /// with HP 0 is the death panel, which the normal-attack kill site sends). It goes to the players who have the
    /// monster selected (see also ReqActorDetailInfoHandler.PushIconToWatchersAsync). Here the
    /// target is the threat table's <see cref="NpcRuntimeState.AggroTargetHandle"/>, the value 2630
    /// body+4 already reports.
    /// </para>
    /// <para>
    /// Entries for dead or vanished monsters are dropped first, so a respawned monster's first target
    /// is always a change from none.
    /// </para>
    /// </remarks>
    private async Task PushTargetChangesAsync(IEnumerable<uint> npcHandles, CancellationToken ct)
    {
        foreach (var handle in _iconTarget.Keys.ToArray())
            if (_npcState.GetNpc(handle) is not { IsDead: false })
                _iconTarget.Remove(handle);

        foreach (var handle in npcHandles)
        {
            var npc = _npcState.GetNpc(handle);
            if (npc is null || npc.IsDead)
                continue;
            var target = npc.AggroTargetHandle;
            if (_iconTarget.GetValueOrDefault(handle) == target)
                continue;
            _iconTarget[handle] = target;
            await ReqActorDetailInfoHandler.PushIconToWatchersAsync(_broadcast, _playerManager,
                npc.MapId, npc.EntityOid, ReqActorDetailInfoHandler.BuildIconOf(npc), ct);
            if (ct.IsCancellationRequested)
                return;
        }
    }

    /// <summary>
    /// The Miss_Rate <paramref name="victim"/>'s active effects add (<see cref="EffectService.MissRateOf"/>), 0 when the
    /// container holds no EffectService. EffectService is the <c>AddSingleton&lt;EffectService&gt;()</c> registration in
    /// Program.cs; it is resolved through the scope factory this pass already uses, so the constructor (and its
    /// registration) stays as it is.
    /// </summary>
    private double MissRateOf(PlayerState victim)
    {
        using var scope = _scopeFactory.CreateScope();
        return scope.ServiceProvider.GetService<EffectService>()?.MissRateOf(victim) ?? 0;
    }

    /// <summary>
    /// Picks who a monster swings at: its top-threat target when that player is on the same map and
    /// in the monster's reach, otherwise the iterating player under the same two tests, or <c>null</c> when
    /// neither qualifies.
    /// </summary>
    /// <remarks>
    /// The reach is the MONSTER's (<see cref="NpcRuntimeState.AttackReach"/>), not the victim's weapon range: the
    /// client's range test takes the range from the attacker, so a
    /// bow user is no longer hit by a melee monster from 24 units. The iterating branch compares maps too: a player's
    /// stale <see cref="PlayerState.TargetEntityId"/> naming a monster of another map at the same x/y must not draw
    /// its swing.
    /// </remarks>
    internal PlayerState? SelectVictim(NpcRuntimeState npc, PlayerState? iterating)
    {
        var aggro = ResolvePlayer(npc.AggroTargetHandle);
        if (aggro is not null && !aggro.IsDead && aggro.MapId == npc.MapId && InNpcReach(npc, aggro))
            return aggro;

        if (iterating is not null && iterating.MapId == npc.MapId && InNpcReach(npc, iterating))
            return iterating;

        return null;
    }

    /// <summary>Whether <paramref name="player"/> stands within the monster's horizontal <see cref="NpcRuntimeState.AttackReach"/>
    /// (the melee <see cref="Reach"/> for a state with none), plus the same <see cref="AutoAttackState.AttackRangeTolerance"/>
    /// the player's own reach gate adds. Non-finite positions are out of reach.</summary>
    /// <remarks>
    /// A server-side defensive tolerance: it mirrors the player-side range gate (ActorGate.MeleeReach /
    /// AutoAttackState.InRange add AttackRangeTolerance too) so a small-range monster does not whiff on a player
    /// the client considers in range. Whether the same tolerance applies to an NPC's own hit distance is unconfirmed.
    /// </remarks>
    internal static bool InNpcReach(NpcRuntimeState npc, PlayerState player)
    {
        float dx = player.X - npc.PosX;
        float dy = player.Y - npc.PosY;
        float reach = (npc.AttackReach > 0f ? npc.AttackReach : Reach) + AutoAttackState.AttackRangeTolerance;
        return MathF.Sqrt(dx * dx + dy * dy) <= reach;
    }

    /// <summary>Sight radius for an aggressive monster whose NPCInfo row carries no Reaction_Bound: the modal hostile
    /// Reaction_Bound. It is the radius the server uses for acquisition.</summary>
    private const float DefaultSightRadius = 9f;

    /// <summary>Threat seated when a monster acquires a player by proximity: one point, enough to make that
    /// player its target. A real hit then banks its threat on top (<see cref="NpcStateManager.AddThreat"/>).</summary>
    private const long AcquireThreat = 1;

    /// <summary>
    /// The nearest living player on <paramref name="onMap"/> that <paramref name="npc"/> may acquire: inside its
    /// sight radius (NPCInfo.Reaction_Bound, else <see cref="DefaultSightRadius"/>) and allowed by its aggression
    /// mode (<see cref="NpcAggression.Acquires"/> — always, or while the player is not too far above it in level).
    /// Null when none qualifies.
    /// </summary>
    private PlayerState? AcquireNearest(NpcRuntimeState npc, PlayerState[] onMap)
    {
        float sight = npc.SightRadius > 0f ? npc.SightRadius : DefaultSightRadius;
        float best = sight * sight;
        PlayerState? nearest = null;
        foreach (var p in onMap)
        {
            if (!NpcAggression.Acquires(npc.Aggression, npc.AggressionLevelGap, npc.Level, p.Level))
                continue;
            float dx = p.X - npc.PosX, dy = p.Y - npc.PosY;
            float d2 = dx * dx + dy * dy;
            if (float.IsFinite(d2) && d2 <= best)
            {
                best = d2;
                nearest = p;
            }
        }
        return nearest;
    }

    /// <summary>
    /// Re-points <paramref name="npc"/> at the highest-threat attacker that is still a live player on its map,
    /// used when its current target has died or left the map (the "switch to the second" rule). Returns the new
    /// target handle, or 0 when no row qualifies (the caller then resets the monster). Under the monster's threat
    /// lock, like every target write; a dead or departed row is skipped but kept on the table (it may return).
    /// </summary>
    private uint ReselectLiveTarget(NpcRuntimeState npc)
    {
        lock (npc.ThreatLock)
        {
            uint best = 0;
            long bestThreat = long.MinValue;
            foreach (var kv in npc.Threat)
            {
                var p = ResolvePlayer(kv.Key);
                if (p is null || p.IsDead || p.MapId != npc.MapId)
                    continue;
                if (kv.Value.Threat > bestThreat)
                {
                    bestThreat = kv.Value.Threat;
                    best = kv.Key;
                }
            }
            npc.AggroTargetHandle = best;
            return best;
        }
    }

    /// <summary>
    /// Resolves a PC actor handle back to its live player. PC handles are
    /// <c>(sessionId &lt;&lt; 8) | 0x11</c> (see <see cref="PlayerStateManager.BuildActorHandle"/>),
    /// so the session id is the handle shifted right by 8.
    /// </summary>
    private PlayerState? ResolvePlayer(uint pcHandle)
    {
        if (pcHandle == 0)
            return null;
        // The EntityId check rejects a reused session slot: a logout does not clear monster threat, so a stale
        // handle must not resolve to the new player who took the slot (same guard as NpcChatService.HasLiveTarget).
        var p = _playerManager.GetPlayer(pcHandle >> ActorTagBits);
        return p is not null && p.EntityId == pcHandle ? p : null;
    }
}
