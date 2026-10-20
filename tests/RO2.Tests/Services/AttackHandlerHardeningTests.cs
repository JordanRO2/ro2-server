using Microsoft.Extensions.Logging.Abstractions;
using RO2.Domain.GameData;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Services;

/// <summary>
/// Cluster B of the combat hardening, the player attack handlers (1499 NormalAttackDamageHandler, 1360
/// ArcherAttackHandler): the swing binding and interval floor (NormalAttackDamageHandler.TakeImpact), the
/// alive / same-map / in-reach gate (GateBasicAttack) and the NPC's real SP in the 2696 (BuildNpcHit2696). Pure: the
/// decisions are static so no session or socket is needed.
/// </summary>
public class AttackHandlerHardeningTests
{
    private const uint MapA = 1100;
    private const uint MapB = 1200;
    private static readonly TimeSpan Floor = AutoAttackState.DefaultMinSwingInterval;
    private static readonly DateTime T0 = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

    private static PlayerState Hero(uint sid, uint map = MapA, float x = 500f, float y = 500f)
        => new() { SessionId = sid, EntityId = PlayerStateManager.BuildActorHandle(sid), MapId = map, X = x, Y = y, Hp = 100, Mp = 50 };

    private static NpcRuntimeState AddMonster(uint map, int index, float x = 501f, float y = 500f)
    {
        var npcs = new NpcStateManager(new GameDataStore(), NullLogger<NpcStateManager>.Instance);
        npcs.TrackSpawns(map, [new SpawnEntry { MapId = map, Index = index, AssetIndex = 0, PosX = x, PosY = y }]);
        var npc = npcs.GetNpc(npcs.AllocateHandle(map, index))!;
        npc.Camp = 2;
        npc.MaxHp = 1000;
        npc.Hp = 1000;
        return npc;
    }

    /// <summary>Arms a swing as a granted 1498 does, then pins its arming time so the clock is the test's.</summary>
    private static DateTime Arm(PlayerState p, uint target, DateTime at)
    {
        AutoAttackState.Engage(p, target);
        p.LastAttackRequestAt = at;
        return at;
    }

    private static NormalAttackDamageHandler.SwingVerdict Impact(PlayerState p, uint target, DateTime at)
        => NormalAttackDamageHandler.TakeImpact(p, target, at);

    // ---- swing binding + interval floor ----------------------------------------------------------

    [Fact]
    public void ImpactOnTheBoundTarget_PastTheFloor_IsAccepted_ThenSpent()
    {
        var p = Hero(0xB10001);
        Arm(p, 0x0000D413, T0);
        Assert.Equal(NormalAttackDamageHandler.SwingVerdict.Accepted, Impact(p, 0x0000D413, T0 + Floor));
        Assert.Equal(NormalAttackDamageHandler.SwingVerdict.NoSwingPending,
            Impact(p, 0x0000D413, T0 + Floor + TimeSpan.FromMilliseconds(40)));
    }

    [Fact]
    public void ImpactOnAnotherTarget_IsRefused_AndSpendsTheSwing()
    {
        var p = Hero(0xB10002);
        Arm(p, 0x0000D413, T0);
        Assert.Equal(NormalAttackDamageHandler.SwingVerdict.WrongTarget, Impact(p, 0x0000D513, T0 + Floor));
        Assert.Equal(NormalAttackDamageHandler.SwingVerdict.NoSwingPending, Impact(p, 0x0000D413, T0 + Floor));
    }

    [Fact]
    public void NeverGranted_NoSwingPending()
        => Assert.Equal(NormalAttackDamageHandler.SwingVerdict.NoSwingPending, Impact(Hero(0xB10003), 0x0000D413, T0));

    [Fact]
    public void TooEarlyImpact_IsNotSpent_AndLandsOnceTheFloorPasses()
    {
        var p = Hero(0xB10004);
        // The first swing lands immediately (the floor is between consecutive ACCEPTED swings, not arm-to-impact).
        Arm(p, 0x0000D413, T0);
        Assert.Equal(NormalAttackDamageHandler.SwingVerdict.Accepted, Impact(p, 0x0000D413, T0));
        // A second swing inside the floor of the first is refused without being spent...
        Arm(p, 0x0000D413, T0 + TimeSpan.FromMilliseconds(20));
        Assert.Equal(NormalAttackDamageHandler.SwingVerdict.TooEarly,
            Impact(p, 0x0000D413, T0 + TimeSpan.FromMilliseconds(20)));
        // ...and lands once the floor passes since the first accepted swing.
        Assert.Equal(NormalAttackDamageHandler.SwingVerdict.Accepted, Impact(p, 0x0000D413, T0 + Floor));
    }

    [Fact]
    public void PairsEvery20ms_AtMostOneImpactPerFloorInterval()
    {
        // A modified client: 1498 + 1499 every 20 ms for 2 s. Re-arming does not reset the accepted-swing clock, so a
        // swing lands only once the floor has passed since the last ACCEPTED one: about one per 200 ms over 2 s.
        var p = Hero(0xB10005);
        int taken = 0;
        for (int ms = 0; ms < 2000; ms += 20)
        {
            Arm(p, 0x0000D413, T0 + TimeSpan.FromMilliseconds(ms));
            if (Impact(p, 0x0000D413, T0 + TimeSpan.FromMilliseconds(ms + 20)) == NormalAttackDamageHandler.SwingVerdict.Accepted)
                taken++;
        }
        Assert.InRange(taken, 9, 11);

        // One 1498, then an impact every 20 ms for 2 s: only the first past the floor lands, the rest are refused.
        var q = Hero(0xB10006);
        Arm(q, 0x0000D413, T0);
        taken = 0;
        for (int ms = 20; ms <= 2000; ms += 20)
            if (Impact(q, 0x0000D413, T0 + TimeSpan.FromMilliseconds(ms)) == NormalAttackDamageHandler.SwingVerdict.Accepted)
                taken++;
        Assert.Equal(1, taken);

        // A client that waits the floor between each 1498 and its impact is held to one impact per 1498.
        var r = Hero(0xB10007);
        taken = 0;
        for (int i = 0; i < 10; i++)
        {
            var at = Arm(r, 0x0000D413, T0 + TimeSpan.FromMilliseconds(i * 300));
            if (Impact(r, 0x0000D413, at + Floor) == NormalAttackDamageHandler.SwingVerdict.Accepted) taken++;
            Assert.NotEqual(NormalAttackDamageHandler.SwingVerdict.Accepted,
                Impact(r, 0x0000D413, at + Floor + TimeSpan.FromMilliseconds(20)));
        }
        Assert.Equal(10, taken);
    }

    [Fact]
    public void ArcherWithoutA1498_FiftyRequests_NothingTaken()
    {
        // 1360 shares TakeImpact: 50 requests with no 1498 take no swing, so the handler returns before any damage.
        var p = Hero(0xB10008);
        for (int i = 0; i < 50; i++)
            Assert.NotEqual(NormalAttackDamageHandler.SwingVerdict.Accepted,
                Impact(p, 0x0000D413, T0 + TimeSpan.FromMilliseconds(i * 20)));
    }

    // ---- map / reach / liveness gate -------------------------------------------------------------

    [Fact]
    public void NpcOnAnotherMap_SameXY_IsRefused_CrossMap()
    {
        var p = Hero(0xB10010, MapA);
        var npc = AddMonster(MapB, 1, x: 500f, y: 500f);   // same x/y as the player, other map
        Assert.Equal(ActorRefusal.CrossMap, NormalAttackDamageHandler.GateBasicAttack(p, npc));
    }

    [Fact]
    public void NpcOnTheSameMap_InReach_Passes()
        => Assert.Equal(ActorRefusal.None,
            NormalAttackDamageHandler.GateBasicAttack(Hero(0xB10011), AddMonster(MapA, 2, 503f, 500f)));

    [Fact]
    public void OutOfReach_DeadNpc_DeadAttacker_AreRefused()
    {
        var p = Hero(0xB10012);
        Assert.Equal(ActorRefusal.OutOfRange, NormalAttackDamageHandler.GateBasicAttack(p, AddMonster(MapA, 3, 540f, 500f)));

        var corpse = AddMonster(MapA, 4);
        corpse.IsDead = true;
        Assert.Equal(ActorRefusal.TargetDead, NormalAttackDamageHandler.GateBasicAttack(p, corpse));

        var ghost = Hero(0xB10013);
        ghost.IsDead = true;
        Assert.Equal(ActorRefusal.ActorDead, NormalAttackDamageHandler.GateBasicAttack(ghost, AddMonster(MapA, 5)));
    }

    [Fact]
    public void BowReach_AdmitsWhatMeleeRefuses()
    {
        var npc = AddMonster(MapA, 6, 515f, 500f);
        Assert.Equal(ActorRefusal.OutOfRange, NormalAttackDamageHandler.GateBasicAttack(Hero(0xB10014), npc));
        var archer = Hero(0xB10015);
        archer.AutoAttackRange = 20;
        Assert.Equal(ActorRefusal.None, NormalAttackDamageHandler.GateBasicAttack(archer, npc));
    }

    // ---- 2696 victim SP --------------------------------------------------------------------------

    [Fact]
    public void Hit2696_CarriesTheNpcRealSp_NotZero()
    {
        var p = Hero(0xB10020);
        var npc = AddMonster(MapA, 7);
        npc.MaxSp = 321;
        var body = NormalAttackDamageHandler.BuildNpcHit2696(p, npc, 25, 1);

        Assert.Equal(321u, SkillDamagePackets.NpcTargetSp(npc));
        Assert.Equal(CombatPackets.BuildAttackDamage2696(p.EntityId, 100, 50, npc.EntityOid, 1000, 321, 25, 1), body);
        Assert.NotEqual(CombatPackets.BuildAttackDamage2696(p.EntityId, 100, 50, npc.EntityOid, 1000, 0, 25, 1), body);
    }
}
