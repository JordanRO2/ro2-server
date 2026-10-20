using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RO2.Domain.GameData;
using RO2.Network.Protocol;
using RO2.Server.Zone.Models;

namespace RO2.Server.Zone.Services;

/// <summary>
/// Makes an aggroed monster CHASE its target: step toward the player it is on until it is within swing reach,
/// and leash home (heal + drop aggro) when dragged past its <see cref="NpcRuntimeState.LeashBound"/>.
/// </summary>
/// <remarks>
/// <para>
/// WHY THIS IS A SEPARATE SERVICE, and why it is safe now. NpcMovementService deliberately does NOT move a
/// monster in combat ("stand and fight", NpcMovementService.cs): an earlier attempt desynchronised the client,
/// because a 2318 carries a navmesh CELL and the only cell the server knew was the SPAWN's, which goes stale the
/// moment the monster leaves it — the actor then skated. That is fixed: <see cref="GameDataStore.GetNavCellAt"/>
/// resolves the cell of ANY point from the map navmesh (NavMesh / data/navmesh), so a chase 2318 now
/// carries the cell the monster is actually on. This service owns the chase step and the leash; NpcCombatService
/// still owns target selection, the swing, and the reset when a target is simply gone.
/// </para>
/// <para>
/// Cadence is its own, between the 2 s wander and the 400 ms swing, so steps are short and the client's walk
/// animation keeps up without the server teleporting the monster far ahead of it. A step is <c>speed ·
/// interval</c>, and the 2318's speed field is that same speed, so the client covers one step in about one tick.
/// The monster stops at its attack reach, where NpcCombatService's swing takes over.
/// </para>
/// <para>
/// Not data-driven: the chase cadence, the run speed fallback, and that a monster returns to its spawn in one
/// straight move on leash. The cell each 2318 carries comes from the navmesh, so the skate that stopped
/// movement before does not return; the rest is this server's movement model, behind
/// <see cref="ChaseEnabled"/> so it can be switched off if a live client shows a problem.
/// </para>
/// </remarks>
public sealed class NpcChaseService : ITickable
{
    private readonly NpcStateManager _npcState;
    private readonly PlayerStateManager _playerManager;
    private readonly BroadcastService _broadcast;
    private readonly GameDataStore _data;
    private readonly ILogger<NpcChaseService> _logger;

    /// <summary>How often the chase pass runs, independent of the loop's tick rate.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(450);

    /// <summary>Master switch. Off falls back to the old "stand and fight" (monsters never move in combat).</summary>
    private static readonly bool ChaseEnabled = true;

    /// <summary>Run speed for a monster whose NPCInfo row carries none (Run_Speed 0). A plausible run pace; the
    /// 2318 speed field only sets how fast the client animates the step.</summary>
    private const float FallbackSpeed = 5.0f;

    /// <summary>Highest NPCInfo.Camp of a friendly NPC. A Camp above this is a monster, the only kind that aggros and chases.</summary>
    private const int MaxFriendlyCamp = 1;

    /// <summary>AggroTargetHandle value of a monster with no target.</summary>
    private const uint NoTargetHandle = 0;

    /// <summary>Bit width of the actor type tag in the low byte of a handle; the session id sits above it.</summary>
    private const int ActorHandleTagBits = 8;

    /// <summary>Longest backlog, in chase intervals, kept while the previous pass is still running.</summary>
    private const int MaxCatchUpIntervals = 4;

    private double _accum;
    private int _running;

    public string Name => "NpcChase";

    public NpcChaseService(NpcStateManager npcState, PlayerStateManager playerManager, BroadcastService broadcast,
        GameDataStore data, ILogger<NpcChaseService> logger)
    {
        _npcState = npcState;
        _playerManager = playerManager;
        _broadcast = broadcast;
        _data = data;
        _logger = logger;
    }

    public void Tick(double deltaSeconds)
    {
        if (!ChaseEnabled)
            return;
        _accum += deltaSeconds;
        double interval = Interval.TotalSeconds;
        if (_accum < interval)
            return;
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            if (_accum > interval * MaxCatchUpIntervals) _accum = interval * MaxCatchUpIntervals;
            return;
        }
        _accum = 0;
        _ = RunTickAsync();
    }

    private async Task RunTickAsync()
    {
        try
        {
            await TickAsync(Interval.TotalSeconds, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NPC chase tick failed");
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private async Task TickAsync(double dt, CancellationToken ct)
    {
        foreach (var mapId in _playerManager.GetAllPlayers().Select(p => p.MapId).Distinct())
        {
            // No navmesh artifact for this map: keep "stand and fight". A chase 2318 would have no cell to carry
            // but the stale spawn cell (GetNavCellAt returns null), which is the skate that stopped in-combat
            // movement in the first place. A monster can only be dragged out to leash by a chase step, so with
            // the step suppressed the leash below never triggers either.
            if (_data.GetNavMesh(mapId) is null)
                continue;

            foreach (var npc in _npcState.GetAliveNpcs(mapId).ToArray())
            {
                if (npc.Camp <= MaxFriendlyCamp || npc.AggroTargetHandle == NoTargetHandle || npc.Spawn is null)
                    continue;

                // (1) LEASH. Dragged past its leash bound from the spawn: it gives up, runs home in one move and
                // resets (heals + drops aggro). The combat service does not leash (its target is still valid); the
                // chat sweep sends the leave-combat 2398 once this clears the target.
                float homeDx = npc.PosX - npc.Spawn.PosX, homeDy = npc.PosY - npc.Spawn.PosY;
                if (npc.LeashBound > 0f && MathF.Sqrt(homeDx * homeDx + homeDy * homeDy) > npc.LeashBound)
                {
                    await MoveAsync(npc, npc.Spawn.PosX, npc.Spawn.PosY, SpeedOf(npc),
                        destCell: _data.GetNavCell(mapId, npc.Spawn.Index) ?? NavCellAt(npc, npc.Spawn.PosX, npc.Spawn.PosY));
                    if (_npcState.LeashReset(npc.EntityOid))
                        // Show the healed HP now (NPC 2401); otherwise the bar stays stale until the next hit.
                        await ActorVitalsBroadcast.SendNpcAutoHealAsync(_broadcast, npc);
                    continue;
                }

                var target = ResolvePlayer(npc.AggroTargetHandle);
                if (target is null || target.IsDead || target.MapId != npc.MapId)
                    continue;   // target gone: the combat service re-selects or resets it

                // (2) CHASE. Close the gap down to the monster's own attack reach, where the swing takes over. No
                // step when already in reach (stand and fight) or the target is non-finite.
                float reach = npc.AttackReach > 0f ? npc.AttackReach : NpcCombatService.Reach;
                float gap = Distance(npc.PosX, npc.PosY, target.X, target.Y);
                if (!float.IsFinite(gap) || gap <= reach)
                    continue;

                float speed = SpeedOf(npc);
                float step = MathF.Min(gap - reach, speed * (float)dt);
                if (step <= 0f)
                    continue;

                var (dx, dy) = StepToward(npc.PosX, npc.PosY, target.X, target.Y, step);
                await MoveAsync(npc, dx, dy, speed, destCell: NavCellAt(npc, dx, dy));

                if (ct.IsCancellationRequested)
                    return;
            }
        }
    }

    /// <summary>Sends one 2318 move of <paramref name="npc"/> to (<paramref name="dx"/>, <paramref name="dy"/>)
    /// with the origin cell resolved from where it stands now and <paramref name="destCell"/> for the
    /// destination, then advances the server-authoritative position, walk and cell to the destination.</summary>
    private async Task MoveAsync(NpcRuntimeState npc, float dx, float dy, float speed, int? destCell)
    {
        var originCell = NavCellAt(npc, npc.PosX, npc.PosY) ?? npc.NavCell;
        var body = NpcSpawnService.BuildOnMoveNpc2318(
            npc.EntityOid, npc.PosX, npc.PosY, originCell, dx, dy, destCell ?? originCell, speed);
        await _broadcast.BroadcastToMapAsync(npc.MapId, ZoneS2COpcodes.OnMoveNPC, w => w.WriteBytes(body));

        npc.LastMove = new NpcMove(npc.PosX, npc.PosY, dx, dy, DateTime.UtcNow, speed);
        npc.PosX = dx;
        npc.PosY = dy;
        if (destCell is int c)
            npc.NavCell = c;
    }

    /// <summary>The navmesh cell of a point on this NPC's map, using the spawn's height for the overlap tiebreak
    /// (the NPC's own Z is not tracked). Null when the map has no navmesh artifact.</summary>
    private int? NavCellAt(NpcRuntimeState npc, float x, float y)
        => _data.GetNavCellAt(npc.MapId, x, y, npc.Spawn?.PosZ ?? 0f);

    /// <summary>Run speed for a 2318: NPCInfo.Run_Speed, else <see cref="FallbackSpeed"/>.</summary>
    private float SpeedOf(NpcRuntimeState npc)
    {
        var run = _data.Npcs.GetValueOrDefault(npc.NpcId)?.Run_Speed ?? 0f;
        return run > 0f ? run : FallbackSpeed;
    }

    /// <summary>A point <paramref name="step"/> units from (fromX, fromY) toward (toX, toY); the origin itself
    /// when they coincide. Pure, so the step geometry is unit-tested without a world.</summary>
    internal static (float X, float Y) StepToward(float fromX, float fromY, float toX, float toY, float step)
    {
        float dx = toX - fromX, dy = toY - fromY;
        float dist = MathF.Sqrt(dx * dx + dy * dy);
        if (dist <= 0f || !float.IsFinite(dist))
            return (fromX, fromY);
        float t = MathF.Min(1f, step / dist);   // never step past the target
        return (fromX + dx * t, fromY + dy * t);
    }

    private static float Distance(float ax, float ay, float bx, float by)
    {
        float dx = bx - ax, dy = by - ay;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>PC actor handle -> live player: PC handles are <c>(sessionId &lt;&lt; 8) | <see cref="PlayerStateManager.PcActorTag"/></c> (0x11).</summary>
    private PlayerState? ResolvePlayer(uint pcHandle)
    {
        if (pcHandle == NoTargetHandle)
            return null;
        // The EntityId check rejects a reused session slot: a logout does not clear monster threat, so a stale
        // handle must not resolve to the new player who took the slot (same guard as NpcChatService.HasLiveTarget).
        var p = _playerManager.GetPlayer(pcHandle >> ActorHandleTagBits);
        return p is not null && p.EntityId == pcHandle ? p : null;
    }
}
