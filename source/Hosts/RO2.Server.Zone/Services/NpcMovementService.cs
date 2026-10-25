using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RO2.GameData;
using RO2.Network.Protocol;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Makes spawned NPCs wander (RMI 2318 OnMoveNPC) and brings dead ones back (2317 OnRegenNPC, under a
/// fresh handle), by pushing to the players who can see them.
/// </summary>
/// <remarks>
/// <para>
/// NOTHING RESPAWNED NPCs BEFORE. <c>NpcStateManager.GetRespawnReady()</c> had zero callers in the
/// whole repo, so a monster killed once stayed dead for the lifetime of the process. The respawn
/// tick lives here because this service already walks the NPCs of every populated map on a timer;
/// the announcement itself belongs to <c>NpcSpawnService.SendNpcRespawnAsync</c>, so there is one
/// place that decides WHAT goes on the wire and one that decides WHEN.
/// </para>
/// <para>
/// Without it the world would be populated but frozen: 2506 OnNPC_In places every NPC and nothing else
/// moves one. 2318 is a high-volume S2C opcode, so a static world would look wrong.
/// </para>
/// <para>
/// WHAT IS DATA-DRIVEN HERE AND WHAT IS NOT. The message layout, its 42-byte length, the
/// coordinate encoding, the navmesh-cell semantics of the third component, and the speed values are
/// described in <c>NpcSpawnService.BuildOnMoveNpc2318</c>. The
/// DESTINATIONS ARE SYNTHESISED: no path or patrol data exists to drive
/// them. Every ZoneSpawn row has TotalPathPoints == 0, so there is nothing to follow. What the data does
/// give us is <c>HoverRadius</c>, which many spawn rows carry, so wandering is confined to it and only
/// NPCs that declare one move at all. Anything with HoverRadius == 0 is a fixture — a shopkeeper, a quest
/// giver — and stays put.
/// </para>
/// <para>
/// Steps are capped at <see cref="NpcSpawnService.MaxWanderStep"/> because the cell we can look up
/// belongs to the spawn point: a short step keeps our reused cell honest. A long teleport across the map
/// would not.
/// </para>
/// <para>
/// Sent UNENC on the internal RMI path, like the rest of the notify traffic.
/// </para>
/// </remarks>
public class NpcMovementService : ITickable
{
    private readonly NpcStateManager _npcState;
    private readonly PlayerStateManager _playerManager;
    private readonly BroadcastService _broadcast;
    private readonly GameDataStore _data;
    private readonly NpcSpawnService _spawn;
    private readonly ILogger<NpcMovementService> _logger;

    /// <summary>How often the wander pass runs, regardless of the loop's tick rate.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    /// <summary>Simulated time banked since the last pass, a re-entrancy guard for the async wander
    /// batch, and the wander RNG (only ever touched by the guarded pass, so effectively single-threaded).</summary>
    private double _accum;
    private int _running;
    private readonly Random _rng = new();

    public string Name => "NpcMovement";

    /// <summary>Bounds on how long an NPC rests between wander steps.</summary>
    private static readonly TimeSpan MinRest = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan MaxRest = TimeSpan.FromSeconds(14);

    /// <summary>
    /// Cap on NPCs moved per map per tick. A busy map holds hundreds of spawns and moving them all at once
    /// would put a burst of that many frames on the wire for no visual gain, so movement is spread out.
    /// </summary>
    private const int MaxMovesPerMapPerTick = 24;

    /// <summary>Speed used when the NPC's template has none. 1.0 is the default walk speed.</summary>
    private const float DefaultSpeed = 1.0f;

    /// <summary>Wander switch (true = NPCs wander). When false, the arrival-skate experiment gate in TickAsync skips wandering.</summary>
    private static readonly bool WanderEnabled = true;

    public NpcMovementService(NpcStateManager npcState, PlayerStateManager playerManager,
        BroadcastService broadcast, GameDataStore data, NpcSpawnService spawn,
        ILogger<NpcMovementService> logger)
    {
        _npcState = npcState;
        _playerManager = playerManager;
        _broadcast = broadcast;
        _data = data;
        _spawn = spawn;
        _logger = logger;
    }

    /// <summary>Driven by the central game loop. Banks simulated time and runs the wander + respawn
    /// pass every <see cref="Interval"/>, independent of the loop's current Hz. The pass broadcasts
    /// asynchronously, so it is fired off the loop thread (fire-and-forget) with a re-entrancy guard.</summary>
    public void Tick(double deltaSeconds)
    {
        _accum += deltaSeconds;
        double interval = Interval.TotalSeconds;
        if (_accum < interval) return;

        // Previous pass still broadcasting — keep the banked time (capped) and retry next tick.
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            if (_accum > interval * 4) _accum = interval * 4;
            return;
        }
        _accum = 0;
        _ = RunTickAsync();
    }

    private async Task RunTickAsync()
    {
        try
        {
            // A failure moving one NPC must not take the service down for the whole world.
            await TickAsync(_rng, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NPC movement tick failed");
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private async Task TickAsync(Random rng, CancellationToken ct)
    {
        // Only maps with someone on them: moving NPCs nobody can see is pure wire traffic.
        var maps = _playerManager.GetAllPlayers().Select(p => p.MapId).Distinct().ToArray();
        var now = DateTime.UtcNow;

        await RespawnAsync(maps, ct);

        // An NPC someone is fighting must NOT wander. Diagnosed live: a player would run to a
        // target, arrive, stay in the RUN animation for several seconds and only then swing —
        // because the server kept walking the monster out from under them and the client kept
        // chasing. Moving during that window desynchronised the client's state entirely.
        var engaged = new HashSet<uint>();
        foreach (var p in _playerManager.GetAllPlayers())
            if (p.IsInCombat && p.TargetEntityId != 0)
                engaged.Add(p.TargetEntityId);

        foreach (var mapId in maps)
        {
            var moved = 0;
            foreach (var npc in _npcState.GetAliveNpcs(mapId))
            {
                if (moved >= MaxMovesPerMapPerTick)
                    break;
                if (npc.Spawn is null || npc.Spawn.HoverRadius <= 0f)
                    continue;   // a fixture, not a wanderer

                // Never move an actor the client could not instantiate. 2506 announces the NPC, but the client
                // discards the record when the asset has no NPCInfo row, so the actor never exists on its side. A 2318
                // for a handle the client does not know makes it send C2S 1306 ReqActorViewData, and it retries
                // forever. So an asset with no NPCInfo row is never moved (nor, by the same rule, is it announced:
                // filtering the 2506 alone is not enough, because the 2318 is the trigger).
                if (!_data.Npcs.ContainsKey(npc.Spawn.AssetIndex))
                    continue;
                if (engaged.Contains(npc.EntityOid))
                    continue;   // in combat: stand and fight
                if (npc.NextMoveAt > now)
                    continue;

                // The skate was the stale cell: wandering moved npc.PosX/PosY but left npc.NavCell at the SPAWN
                // cell, so the 2318 shipped new coords with a cell that no longer matched. That is now fixed —
                // the step below carries the destination's REAL cell from the navmesh solver (GetNavCellAt) and
                // updates npc.NavCell to it. The switch stays as a kill-switch for the whole wander pass.
                if (!WanderEnabled)
                    continue;

                // With the map's navmesh loaded, the 2318 can carry the DESTINATION's real cell (GetNavCellAt),
                // so the wander no longer skates: the step is no longer pinned to the spawn cell's short range and
                // the NPC walks a natural path anywhere inside its HoverRadius. Without a .nav we keep the old
                // short step that reuses the spawn cell (the only safe choice when the destination's cell is unknown).
                var mesh = _data.GetNavMesh(mapId) is not null;
                var (dx, dy) = PickDestination(rng, npc, mesh);
                int? originCell = mesh
                    ? _data.GetNavCellAt(mapId, npc.PosX, npc.PosY, npc.Spawn.PosZ) ?? npc.NavCell
                    : npc.NavCell;
                int? destCell = mesh
                    ? _data.GetNavCellAt(mapId, dx, dy, npc.Spawn.PosZ) ?? originCell
                    : npc.NavCell;

                var body = NpcSpawnService.BuildOnMoveNpc2318(
                    npc.EntityOid, npc.PosX, npc.PosY, originCell, dx, dy, destCell, DefaultSpeed);

                await _broadcast.BroadcastToMapAsync(mapId, ZoneS2COpcodes.OnMoveNPC,
                    w => w.WriteBytes(body));

                // The server is authoritative about where the NPC now is, so the next step starts
                // from the destination rather than drifting away from what the client was told.
                // The walk itself is kept too — the same origin, destination and speed the 2318 just
                // carried — so a 1306 that arrives while it is being covered is answered with the NPC
                // ON the walk (NpcSpawnService.BuildOnNpcViewData2319) instead of standing at the destination. The
                // start is read after the broadcast, not the pass's `now`, which the awaited sends of earlier NPCs in
                // this pass have already left behind.
                npc.LastMove = new NpcMove(npc.PosX, npc.PosY, dx, dy, DateTime.UtcNow, DefaultSpeed);
                npc.PosX = dx;
                npc.PosY = dy;
                if (mesh && destCell is int cell)
                    npc.NavCell = cell;   // the NPC now stands on the destination's real cell; keep 2319/2506 honest
                npc.NextMoveAt = now + TimeSpan.FromSeconds(
                    MinRest.TotalSeconds + rng.NextDouble() * (MaxRest - MinRest).TotalSeconds);
                moved++;

                if (ct.IsCancellationRequested)
                    return;
            }
        }
    }

    /// <summary>
    /// Brings dead NPCs back under a fresh handle and announces them with 2317 OnRegenNPC; the corpse keeps the
    /// old handle until its own 2507 (NpcStateManager.Respawn).
    /// </summary>
    /// <remarks>
    /// <para>
    /// THIS TICK IS THE RESPAWN TRIGGER. <c>NpcStateManager.GetRespawnReady()</c> has no other caller, so
    /// without it a monster killed once would stay dead for the lifetime of the process and the 2317 builder
    /// would have no trigger. This is the smallest place to put it: the service already iterates NPCs per
    /// populated map on a timer and already broadcasts to those maps.
    /// </para>
    /// <para>
    /// The meaning of 2317 is "this single NPC is now present for you", the singular counterpart of a 2506
    /// list, which is exactly what a respawn needs to say. A respawn gets a FRESH handle each time. The
    /// trigger here is this server's own respawn rule.
    /// </para>
    /// <para>
    /// The 2506 announcement gate applies here too: an asset with no NPCInfo row cannot be
    /// instantiated by the client, and announcing it is what produced the infinite 1306 retry storm.
    /// </para>
    /// </remarks>
    private async Task RespawnAsync(uint[] populatedMaps, CancellationToken ct)
    {
        if (populatedMaps.Length == 0)
            return;

        // The corpse does not hold the respawn back: NpcStateManager.Respawn gives the NPC a fresh handle and
        // leaves the old one to the corpse until the corpse's own 2507.
        var ready = _npcState.GetRespawnReady()
            .Where(n => populatedMaps.Contains(n.MapId))
            .Take(MaxRespawnsPerTick)
            .ToArray();

        foreach (var npc in ready)
        {
            _npcState.Respawn(npc.EntityOid);

            // A respawning NPC returns to its spawn point; the wander walk resumes from there. The walk it
            // was on before it died is over: a 2319 for the fresh handle must describe a standing NPC at
            // the spawn point, not the tail of a walk the dead one never finished.
            npc.LastMove = null;
            if (npc.Spawn is not null)
            {
                npc.PosX = npc.Spawn.PosX;
                npc.PosY = npc.Spawn.PosY;
            }

            // Same predicate the announcement path applies: without an NPCInfo row the client discards the actor
            // and never registers it, and every later 2318 for that handle triggers a 1306 the client retries
            // forever.
            if (npc.Spawn is null || !_data.Npcs.ContainsKey(npc.Spawn.AssetIndex))
                continue;

            // ONE respawn path. NpcSpawnService owns the 2317 and the walk-speed lookup that feeds
            // record+38; this service owns only the decision of WHEN.
            await _spawn.SendNpcRespawnAsync(_broadcast, npc);

            if (ct.IsCancellationRequested)
                return;
        }
    }

    /// <summary>
    /// Cap on respawn announcements per tick, for the same reason movement is capped: a map that
    /// has been farmed hard should trickle back rather than emit one burst of hundreds of frames.
    /// </summary>
    private const int MaxRespawnsPerTick = 16;

    /// <summary>
    /// Picks the next wander destination: a short step, kept inside the spawn's HoverRadius.
    /// </summary>
    /// <remarks>
    /// Two constraints, both load-bearing. The step is capped at
    /// <see cref="NpcSpawnService.MaxWanderStep"/> so the spawn's navmesh cell stays valid for the
    /// destination. The result is clamped into HoverRadius of the SPAWN POINT, not of the current
    /// position, so repeated steps cannot random-walk the NPC across the map.
    /// </remarks>
    internal static (float X, float Y) PickDestination(Random rng, NpcRuntimeState npc, bool mesh = false)
    {
        var angle = rng.NextDouble() * Math.Tau;
        // With the navmesh the destination may be anywhere in HoverRadius (a natural walk the client animates at
        // walk speed); without it the step stays short (MaxWanderStep) so the reused spawn cell is still valid.
        var reach = mesh ? npc.Spawn.HoverRadius : NpcSpawnService.MaxWanderStep;
        var step = (float)(rng.NextDouble() * reach);
        var x = npc.PosX + (float)(Math.Cos(angle) * step);
        var y = npc.PosY + (float)(Math.Sin(angle) * step);

        var ox = npc.Spawn.PosX;
        var oy = npc.Spawn.PosY;
        var r = npc.Spawn.HoverRadius;
        var dist = MathF.Sqrt((x - ox) * (x - ox) + (y - oy) * (y - oy));
        if (dist > r && dist > 0f)
        {
            x = ox + (x - ox) * r / dist;
            y = oy + (y - oy) * r / dist;
        }
        return (x, y);
    }
}
