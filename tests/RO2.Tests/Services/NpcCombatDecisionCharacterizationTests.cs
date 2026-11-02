using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.GameData;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Services;

/// <summary>
/// B6 S1 characterization net: pins what NpcCombatService's target-decision paths do TODAY (bugs included) so the
/// NpcRuntimeState split cannot change them unnoticed. Covers InNpcReach/SelectVictim, AcquireNearest (sight scan)
/// and ReselectLiveTarget. Pure: no sockets, no Mongo, no game data. The two private methods are called by
/// reflection (no production change).
/// <para>
/// NOT pinned: the swing-chain RNG (the parry roll uses <c>Random.Shared</c> inline in the async pass, and the miss
/// arm reads EffectService through the scope factory), so neither can be exercised without a production seam.
/// </para>
/// </summary>
public class NpcCombatDecisionCharacterizationTests
{
    private const uint MapA = 1100;
    private const uint MapB = 1200;
    private const float Tol = 3.890625f;   // AutoAttackState.AttackRangeTolerance

    private sealed class NoDirtySink : IDirtySink
    {
        public void Mark(PlayerState player) { }
        public void Forget(uint characterId) { }
    }

    private static readonly MethodInfo AcquireNearestMi =
        typeof(NpcCombatService).GetMethod("AcquireNearest", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo ReselectMi =
        typeof(NpcCombatService).GetMethod("ReselectLiveTarget", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static (NpcCombatService combat, PlayerStateManager players) New()
    {
        var players = new PlayerStateManager(new NoDirtySink());
        var npcs = new NpcStateManager(new GameDataStore(), NullLogger<NpcStateManager>.Instance);
        var combat = new NpcCombatService(npcs, players, null!, null!, null!, null!, null!,
            NullLogger<NpcCombatService>.Instance);
        return (combat, players);
    }

    private static NpcRuntimeState Npc(float sight = 0f, NpcAggressionMode mode = NpcAggressionMode.Always,
        int level = 10, int gap = 0)
        => new() { MapId = MapA, PosX = 0f, PosY = 0f, Level = level, Aggression = mode,
                   AggressionLevelGap = gap, SightRadius = sight };

    private static PlayerState Pc(PlayerStateManager mgr, uint session, float x, float y, byte level = 10,
        uint map = MapA)
    {
        var p = new PlayerState { SessionId = session, EntityId = PlayerStateManager.BuildActorHandle(session),
                                  MapId = map, X = x, Y = y, Level = level };
        mgr.AddPlayer(session, p);
        return p;
    }

    private static PlayerState? Acquire(NpcCombatService c, NpcRuntimeState npc, params PlayerState[] onMap)
        => (PlayerState?)AcquireNearestMi.Invoke(c, [npc, onMap]);

    private static uint Reselect(NpcCombatService c, NpcRuntimeState npc)
        => (uint)ReselectMi.Invoke(c, [npc])!;

    private static void Seat(NpcRuntimeState npc, PlayerState p, long threat)
        => npc.Threat[p.EntityId] = new NpcStateManager.ThreatRow { AttackerHandle = p.EntityId, Threat = threat };

    // ---- InNpcReach -------------------------------------------------------------------------------

    [Fact]
    public void InNpcReach_Constants_AreTheCurrentOnes()
    {
        Assert.Equal(4f, NpcCombatService.Reach);
        Assert.Equal(3.890625f, AutoAttackState.AttackRangeTolerance);
    }

    [Fact]
    public void InNpcReach_BoundaryIsInclusive_UsesEuclideanDistance()
    {
        var npc = new NpcRuntimeState { PosX = 10f, PosY = 20f, AttackReach = 3f };
        float reach = 3f + Tol;   // 6.890625
        Assert.True(NpcCombatService.InNpcReach(npc, new PlayerState { X = 10f + reach, Y = 20f }));
        Assert.False(NpcCombatService.InNpcReach(npc, new PlayerState { X = 10f + reach + 0.01f, Y = 20f }));
        // (4, 3) offset -> 5.0 in; (5, 5) offset -> 7.07 out.
        Assert.True(NpcCombatService.InNpcReach(npc, new PlayerState { X = 14f, Y = 23f }));
        Assert.False(NpcCombatService.InNpcReach(npc, new PlayerState { X = 15f, Y = 25f }));
    }

    [Fact]
    public void InNpcReach_NonPositiveReach_FallsBackTo4Plus3p890625()
    {
        foreach (var r in new[] { 0f, -2f })
        {
            var npc = new NpcRuntimeState { PosX = 0f, PosY = 0f, AttackReach = r };
            Assert.True(NpcCombatService.InNpcReach(npc, new PlayerState { X = 7.890625f, Y = 0f }));
            Assert.False(NpcCombatService.InNpcReach(npc, new PlayerState { X = 7.9f, Y = 0f }));
        }
    }

    [Fact]
    public void InNpcReach_NonFiniteAxes_AreOut()
    {
        var npc = new NpcRuntimeState { PosX = 0f, PosY = 0f, AttackReach = 50f };
        Assert.False(NpcCombatService.InNpcReach(npc, new PlayerState { X = 0f, Y = float.NaN }));
        Assert.False(NpcCombatService.InNpcReach(npc, new PlayerState { X = float.NegativeInfinity, Y = 0f }));
        Assert.False(NpcCombatService.InNpcReach(new NpcRuntimeState { PosX = float.NaN, AttackReach = 50f },
            new PlayerState { X = 0f, Y = 0f }));
    }

    // ---- SelectVictim -----------------------------------------------------------------------------

    [Fact]
    public void SelectVictim_AggroTargetInReach_WinsOverTheIteratingPlayer()
    {
        var (c, players) = New();
        var npc = Npc(); npc.AttackReach = 3f;
        var top = Pc(players, 1, 2f, 0f);
        var other = Pc(players, 2, 1f, 0f);
        npc.AggroTargetHandle = top.EntityId;
        Assert.Same(top, c.SelectVictim(npc, other));
    }

    [Fact]
    public void SelectVictim_AggroTargetDead_OtherMap_OrOutOfReach_FallsToIterating()
    {
        var (c, players) = New();
        var npc = Npc(); npc.AttackReach = 3f;
        var iter = Pc(players, 2, 1f, 0f);

        var top = Pc(players, 1, 1f, 0f); top.IsDead = true;
        npc.AggroTargetHandle = top.EntityId;
        Assert.Same(iter, c.SelectVictim(npc, iter));

        top.IsDead = false; top.MapId = MapB;
        Assert.Same(iter, c.SelectVictim(npc, iter));

        top.MapId = MapA; top.X = 3f + Tol + 0.01f;
        Assert.Same(iter, c.SelectVictim(npc, iter));

        top.X = 3f + Tol - 0.01f;
        Assert.Same(top, c.SelectVictim(npc, iter));
    }

    [Fact]
    public void SelectVictim_NoAggro_NullIterating_OrStaleHandle_ReturnsNull()
    {
        var (c, players) = New();
        var npc = Npc(); npc.AttackReach = 3f;
        Assert.Null(c.SelectVictim(npc, null));

        var squatter = Pc(players, 5, 0f, 0f);
        npc.AggroTargetHandle = PlayerStateManager.BuildActorHandle(6);   // session 6: not registered
        Assert.Null(c.SelectVictim(npc, null));

        squatter.EntityId = 0xDEAD;                                       // slot reused: EntityId no longer matches
        npc.AggroTargetHandle = PlayerStateManager.BuildActorHandle(5);
        Assert.Null(c.SelectVictim(npc, null));
    }

    [Fact]
    public void SelectVictim_IteratingDeadPlayer_IsStillSelected_TodaysBehaviour()
    {
        // Only the aggro branch tests IsDead; the iterating branch checks map + reach only.
        var (c, players) = New();
        var npc = Npc(); npc.AttackReach = 3f;
        var iter = Pc(players, 2, 1f, 0f); iter.IsDead = true;
        Assert.Same(iter, c.SelectVictim(npc, iter));
    }

    // ---- AcquireNearest ---------------------------------------------------------------------------

    [Fact]
    public void AcquireNearest_DefaultSightIs9_WhenNoReactionBound_BoundaryInclusive()
    {
        var (c, players) = New();
        var npc = Npc(sight: 0f);
        var edge = Pc(players, 1, 9f, 0f);
        Assert.Same(edge, Acquire(c, npc, edge));
        edge.X = 9.01f;
        Assert.Null(Acquire(c, npc, edge));

        npc.SightRadius = -1f;   // non-positive also falls back to 9
        edge.X = 9f;
        Assert.Same(edge, Acquire(c, npc, edge));
    }

    [Fact]
    public void AcquireNearest_UsesTheNpcsSight_WhenSet()
    {
        var (c, players) = New();
        var npc = Npc(sight: 20f);
        var p = Pc(players, 1, 15f, 0f);
        Assert.Same(p, Acquire(c, npc, p));
        npc.SightRadius = 14.99f;
        Assert.Null(Acquire(c, npc, p));
    }

    [Fact]
    public void AcquireNearest_PicksTheNearestBySquaredDistance()
    {
        var (c, players) = New();
        var npc = Npc(sight: 20f);
        var far = Pc(players, 1, 8f, 0f);
        var near = Pc(players, 2, 3f, 4f);   // d = 5
        var mid = Pc(players, 3, 0f, 6f);
        Assert.Same(near, Acquire(c, npc, far, near, mid));
        Assert.Same(near, Acquire(c, npc, mid, near, far));
    }

    [Fact]
    public void AcquireNearest_EqualDistanceTie_GoesToTheLaterPlayer_TodaysBehaviour()
    {
        // The comparison is d2 <= best (not <), so among equals the LAST one scanned wins.
        var (c, players) = New();
        var npc = Npc(sight: 20f);
        var a = Pc(players, 1, 5f, 0f);
        var b = Pc(players, 2, 0f, 5f);
        Assert.Same(b, Acquire(c, npc, a, b));
        Assert.Same(a, Acquire(c, npc, b, a));
    }

    [Fact]
    public void AcquireNearest_NonFinitePositions_AreRejected()
    {
        var (c, players) = New();
        var npc = Npc(sight: 20f);
        var nan = Pc(players, 1, float.NaN, 0f);
        var inf = Pc(players, 2, float.PositiveInfinity, 0f);
        var ok = Pc(players, 3, 10f, 0f);
        Assert.Null(Acquire(c, npc, nan, inf));
        Assert.Same(ok, Acquire(c, npc, nan, inf, ok));
    }

    [Fact]
    public void AcquireNearest_EmptyList_ReturnsNull()
    {
        var (c, _) = New();
        Assert.Null(Acquire(c, Npc()));
    }

    [Fact]
    public void AcquireNearest_AggressionMode_GatesTheCandidates()
    {
        var (c, players) = New();
        var p = Pc(players, 1, 1f, 0f, level: 20);

        Assert.Null(Acquire(c, Npc(mode: NpcAggressionMode.None), p));
        Assert.Same(p, Acquire(c, Npc(mode: NpcAggressionMode.Always, level: 1), p));

        // ByLevel: acquires while playerLevel - npcLevel <= gap (npc level 10, player 20 -> diff 10).
        Assert.Same(p, Acquire(c, Npc(mode: NpcAggressionMode.ByLevel, level: 10, gap: 10), p));
        Assert.Null(Acquire(c, Npc(mode: NpcAggressionMode.ByLevel, level: 10, gap: 9), p));

        // A rejected near player does not block a farther acceptable one.
        var low = Pc(players, 2, 5f, 0f, level: 5);
        Assert.Same(low, Acquire(c, Npc(mode: NpcAggressionMode.ByLevel, level: 10, gap: 5), p, low));
    }

    [Fact]
    public void AcquireNearest_DoesNotCheckMapOrDeath_TheCallerFiltersThose()
    {
        var (c, players) = New();
        var npc = Npc(sight: 20f);
        var elsewhere = Pc(players, 1, 1f, 0f, map: MapB);
        var dead = Pc(players, 2, 2f, 0f); dead.IsDead = true;
        Assert.Same(elsewhere, Acquire(c, npc, elsewhere, dead));
        Assert.Same(dead, Acquire(c, npc, dead));
    }

    // ---- ReselectLiveTarget -----------------------------------------------------------------------

    [Fact]
    public void ReselectLiveTarget_PicksTheHighestThreatLivePlayer_AndWritesTheHandle()
    {
        var (c, players) = New();
        var npc = Npc();
        var a = Pc(players, 1, 0f, 0f); var b = Pc(players, 2, 0f, 0f); var d = Pc(players, 3, 0f, 0f);
        Seat(npc, a, 10); Seat(npc, b, 50); Seat(npc, d, 30);
        Assert.Equal(b.EntityId, Reselect(c, npc));
        Assert.Equal(b.EntityId, npc.AggroTargetHandle);
    }

    [Fact]
    public void ReselectLiveTarget_SkipsDeadAndOtherMap_ButKeepsTheirRows()
    {
        var (c, players) = New();
        var npc = Npc();
        var dead = Pc(players, 1, 0f, 0f); dead.IsDead = true;
        var away = Pc(players, 2, 0f, 0f, map: MapB);
        var live = Pc(players, 3, 0f, 0f);
        Seat(npc, dead, 900); Seat(npc, away, 800); Seat(npc, live, 1);
        Assert.Equal(live.EntityId, Reselect(c, npc));
        Assert.Equal(3, npc.Threat.Count);
    }

    [Fact]
    public void ReselectLiveTarget_NoQualifier_ReturnsZero_AndClearsTheTarget()
    {
        var (c, players) = New();
        var npc = Npc();
        Assert.Equal(0u, Reselect(c, npc));   // empty table

        var dead = Pc(players, 1, 0f, 0f); dead.IsDead = true;
        Seat(npc, dead, 5);
        npc.AggroTargetHandle = dead.EntityId;
        Assert.Equal(0u, Reselect(c, npc));
        Assert.Equal(0u, npc.AggroTargetHandle);
        Assert.Single(npc.Threat);

        // A handle with no registered player (logged out) is skipped too.
        npc.Threat[PlayerStateManager.BuildActorHandle(77)] = new NpcStateManager.ThreatRow { Threat = 100 };
        Assert.Equal(0u, Reselect(c, npc));
    }

    [Fact]
    public void ReselectLiveTarget_EqualThreat_KeepsTheFirstInInsertionOrder()
    {
        var (c, players) = New();
        var npc = Npc();
        var a = Pc(players, 1, 0f, 0f); var b = Pc(players, 2, 0f, 0f);
        Seat(npc, a, 20); Seat(npc, b, 20);
        Assert.Equal(a.EntityId, Reselect(c, npc));   // strict >, no hysteresis
    }

    [Fact]
    public void ReselectLiveTarget_RejectsAReusedSessionSlot()
    {
        var (c, players) = New();
        var npc = Npc();
        var squatter = Pc(players, 4, 0f, 0f);
        squatter.EntityId = 0xABCD;       // slot 4 now holds an EntityId that differs from the key below
        Seat(npc, squatter, 9);           // key 0xABCD -> session 0xAB, unregistered
        npc.Threat[PlayerStateManager.BuildActorHandle(4)] = new NpcStateManager.ThreatRow { Threat = 9 };   // slot 4, EntityId mismatch
        Assert.Equal(0u, Reselect(c, npc));
    }
}
