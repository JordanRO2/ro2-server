using Microsoft.Extensions.Logging.Abstractions;
using RO2.GameData;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Services;

/// <summary>
/// Cluster A of the combat hardening: the atomic NPC kill (NpcStateManager.ApplyDamage under HpLock), the player
/// vitals lock (PlayerState.ApplyHpDelta/ApplySpDelta/SetVitalsDead), the swing binding + interval floor
/// (AutoAttackState.TryConsumeSwing overload) and the NPC's own reach (NpcCombatService.SelectVictim). Pure: no
/// sockets, no game data.
/// </summary>
public class CombatVitalsAndReachTests
{
    private const uint MapA = 1100;
    private const uint MapB = 1200;

    private sealed class NoDirtySink : IDirtySink
    {
        public void Mark(PlayerState player) { }
        public void Forget(uint characterId) { }
    }

    private static NpcStateManager NewNpcs()
        => new(new GameDataStore(), NullLogger<NpcStateManager>.Instance);

    /// <summary>One tracked monster, Camp 2 so it keeps a threat table, at (500, 500) on <see cref="MapA"/>.</summary>
    private static NpcRuntimeState AddMonster(NpcStateManager npcs, int index, int hp)
    {
        npcs.TrackSpawns(MapA, [new SpawnEntry { MapId = MapA, Index = index, AssetIndex = 0, PosX = 500f, PosY = 500f }]);
        var npc = npcs.GetNpc(npcs.AllocateHandle(MapA, index))!;
        npc.Camp = 2;
        npc.MaxHp = hp;
        npc.Hp = hp;
        return npc;
    }

    // ---- atomic ApplyDamage (CB-H5/H6) ---------------------------------------------------------

    [Fact]
    public void EightThreadsLethalApplyDamage_ExactlyOneDied_HpZero()
    {
        var npcs = NewNpcs();
        for (int round = 0; round < 300; round++)
        {
            var npc = AddMonster(npcs, round + 1, hp: 100);
            var oid = npc.EntityOid;
            int died = 0, dealt = 0;
            using var gate = new Barrier(8);
            var threads = Enumerable.Range(0, 8).Select(i => new Thread(() =>
            {
                gate.SignalAndWait();
                // Each attacker alone would kill it; the attacker handle also drives the HpLock -> ThreatLock order.
                var (damage, kill) = npcs.ApplyDamage(oid, 100, 0x11u + (uint)(i + 1) * 0x100, $"p{i}");
                Interlocked.Add(ref dealt, damage);
                if (kill) Interlocked.Increment(ref died);
            })).ToList();
            threads.ForEach(t => t.Start());
            threads.ForEach(t => t.Join());

            Assert.Equal(1, died);
            Assert.Equal(100, dealt);   // the one lethal hit took the 100 HP; every loser was refused with 0
            Assert.Equal(0, npc.Hp);
            Assert.True(npc.IsDead);
            Assert.Empty(npc.Threat);   // the death cleared the table and no loser banked onto it afterwards
        }
    }

    [Fact]
    public void ApplyDamage_ChipsThenOneKill_AcrossThreads()
    {
        var npcs = NewNpcs();
        var npc = AddMonster(npcs, 1, hp: 1000);
        var oid = npc.EntityOid;
        int died = 0, dealt = 0;
        using var gate = new Barrier(8);
        var threads = Enumerable.Range(0, 8).Select(i => new Thread(() =>
        {
            gate.SignalAndWait();
            for (int n = 0; n < 50; n++)
            {
                var (damage, kill) = npcs.ApplyDamage(oid, 7, 0x11u + (uint)(i + 1) * 0x100, $"p{i}");
                Interlocked.Add(ref dealt, damage);
                if (kill) Interlocked.Increment(ref died);
            }
        })).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());

        Assert.Equal(1, died);
        Assert.Equal(1000, dealt);   // no lost update: the damage dealt sums to exactly the HP it had
        Assert.Equal(0, npc.Hp);
    }

    [Fact]
    public void Respawn_ResetsHp_AndAHitOnTheOldHandleIsRefused()
    {
        var npcs = NewNpcs();
        var npc = AddMonster(npcs, 1, hp: 50);
        var oldOid = npc.EntityOid;
        Assert.True(npcs.ApplyDamage(oldOid, 50).Died);

        var fresh = npcs.Respawn(oldOid);

        Assert.NotEqual(0u, fresh);
        Assert.Equal(50, npc.Hp);
        Assert.False(npc.IsDead);
        Assert.Equal((0, false), npcs.ApplyDamage(oldOid, 10));   // the old handle is a corpse, not the new life
        Assert.Equal(50, npc.Hp);
        Assert.Equal((10, false), npcs.ApplyDamage(fresh, 10));
    }

    // ---- player vitals lock ---------------------------------------------------------------------

    [Fact]
    public void ConcurrentApplyHpDelta_LosesNoUpdate()
    {
        var player = new PlayerState { MaxHp = 1_000_000, Hp = 1_000_000, MaxMp = 1_000_000, Mp = 1_000_000 };
        using var gate = new Barrier(8);
        var threads = Enumerable.Range(0, 8).Select(_ => new Thread(() =>
        {
            gate.SignalAndWait();
            for (int n = 0; n < 10_000; n++)
            {
                player.ApplyHpDelta(-1);
                player.ApplySpDelta(-2);
            }
        })).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());

        Assert.Equal(1_000_000 - 8 * 10_000, player.Hp);
        Assert.Equal(1_000_000 - 8 * 10_000 * 2, player.Mp);
    }

    [Fact]
    public void ApplyHpAndSpDelta_ClampToZeroAndMax_AndReturnTheNewValue()
    {
        var player = new PlayerState { MaxHp = 100, Hp = 90, MaxMp = 50, Mp = 10 };

        Assert.Equal(100, player.ApplyHpDelta(500));
        Assert.Equal(0, player.ApplyHpDelta(-500));
        Assert.Equal(0, player.Hp);
        Assert.Equal(50, player.ApplySpDelta(int.MaxValue));
        Assert.Equal(0, player.ApplySpDelta(int.MinValue));
        Assert.Equal(0, player.Mp);
    }

    [Fact]
    public void ApplyHpDelta_WithUnresolvedMax_OnlyFloorsAtZero()
    {
        var player = new PlayerState { Hp = 5000 };   // MaxHp never resolved (0)

        Assert.Equal(4990, player.ApplyHpDelta(-10));
        Assert.Equal(0, player.ApplyHpDelta(-10_000));
    }

    [Fact]
    public void SetVitalsDead_ZeroesHpSp_AndFlagsDead_AndPersists()
    {
        var marks = 0;
        var player = new PlayerState { MaxHp = 100, Hp = 40, MaxMp = 50, Mp = 30 };
        player.PersistSink = new CountingSink(() => marks++);

        player.SetVitalsDead();

        Assert.Equal(0, player.Hp);
        Assert.Equal(0, player.Mp);
        Assert.True(player.IsDead);
        Assert.True(marks >= 1);
    }

    private sealed class CountingSink(Action onMark) : IDirtySink
    {
        public void Mark(PlayerState player) => onMark();
        public void Forget(uint characterId) { }
    }

    [Fact]
    public void TryApplyDelta_RefusesOnADeadPlayer_AndLeavesItDead()
    {
        var player = new PlayerState { MaxHp = 100, Hp = 40, MaxMp = 50, Mp = 30 };
        player.SetVitalsDead();

        Assert.False(player.TryApplyHpDelta(1000, out var hp));
        Assert.Equal(0, hp);
        Assert.False(player.TryApplySpDelta(1000, out var sp));
        Assert.Equal(0, sp);
        Assert.Equal(0, player.Hp);
        Assert.Equal(0, player.Mp);
        Assert.True(player.IsDead);

        // ReviveTo is the explicit resurrection, and heals work again after it.
        player.ReviveTo(100, 50);
        Assert.False(player.IsDead);
        Assert.True(player.TryApplyHpDelta(-10, out hp));
        Assert.Equal(90, hp);
    }

    [Fact]
    public void LethalWrite_IsNotUndone_ByARacingHealOrRegen()
    {
        for (int round = 0; round < 500; round++)
        {
            var player = new PlayerState { MaxHp = 1000, Hp = 1000, MaxMp = 1000, Mp = 1000 };
            using var gate = new Barrier(8);

            // One thread lands the lethal hit, decided and written as one step under the vitals lock exactly as the
            // combat pass does; the others race heals/regen that must refuse (skip) once the player is dead.
            var killer = new Thread(() =>
            {
                gate.SignalAndWait();
                lock (player.Vitals)
                {
                    player.ApplyHpDelta(-player.Hp);
                    player.SetVitalsDead();
                }
            });
            var healers = Enumerable.Range(0, 7).Select(_ => new Thread(() =>
            {
                gate.SignalAndWait();
                for (int n = 0; n < 200; n++)
                {
                    player.TryApplyHpDelta(500, out _);
                    player.TryApplySpDelta(500, out _);
                    RegenService.ApplyRegen(player);
                }
            })).ToList();

            killer.Start();
            healers.ForEach(t => t.Start());
            killer.Join();
            healers.ForEach(t => t.Join());

            // Once the lethal write wins, no heal/regen resurrects the player: each sees IsDead under the lock and skips.
            Assert.True(player.IsDead);
            Assert.Equal(0, player.Hp);
            Assert.Equal(0, player.Mp);
        }
    }

    // ---- swing binding + interval floor ---------------------------------------------------------

    [Fact]
    public void TryConsumeSwing_ReturnsTheBoundTarget_AndTheLatestEngageRebinds()
    {
        var player = new PlayerState { SessionId = 0xC0A001 };
        const uint a = 0x00000A13, b = 0x00000B13;
        Assert.False(AutoAttackState.TryConsumeSwing(player, DateTime.UtcNow, TimeSpan.Zero, out var none));
        Assert.Equal(0u, none);

        AutoAttackState.Engage(player, a);
        AutoAttackState.Engage(player, b);   // a later 1498 re-binds the pending swing
        Assert.True(AutoAttackState.TryConsumeSwing(player, DateTime.UtcNow, TimeSpan.Zero, out var bound));
        Assert.Equal(b, bound);
        Assert.False(AutoAttackState.TryConsumeSwing(player, DateTime.UtcNow, TimeSpan.Zero, out _));   // consumed
    }

    [Fact]
    public void TryConsumeSwing_IntervalFloor_RefusesEarlyWithoutConsuming()
    {
        var player = new PlayerState { SessionId = 0xC0A002 };
        const uint target = 0x00000A13;
        var floor = AutoAttackState.DefaultMinSwingInterval;
        var t0 = new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

        // The first swing lands immediately: the floor is between consecutive ACCEPTED swings, not arm-to-impact.
        AutoAttackState.Engage(player, target);
        Assert.True(AutoAttackState.TryConsumeSwing(player, t0, floor, out var first));
        Assert.Equal(target, first);

        // A second swing inside the floor of the first is refused WITHOUT being spent, and still reports its target...
        AutoAttackState.Engage(player, target);
        Assert.False(AutoAttackState.TryConsumeSwing(player, t0 + floor - TimeSpan.FromMilliseconds(1), floor, out var early));
        Assert.Equal(target, early);
        // ...so once the floor passes it lands (the refusal did not consume the pending swing).
        Assert.True(AutoAttackState.TryConsumeSwing(player, t0 + floor, floor, out var onTime));
        Assert.Equal(target, onTime);
    }

    [Fact]
    public void TryConsumeSwing_PairEvery20ms_NeverLandsTwoWithinTheFloor()
    {
        var player = new PlayerState { SessionId = 0xC0A003 };
        var floor = AutoAttackState.DefaultMinSwingInterval;
        var t0 = new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);
        var accepted = new List<DateTime>();
        // A 1498 + 1499 pair every 20 ms: the first lands, then a swing lands only once the floor has passed since
        // the last ACCEPTED one - so no two accepted swings are ever closer than the floor (anti-flood).
        for (int ms = 0; ms < 1000; ms += 20)
        {
            AutoAttackState.Engage(player, 0x00000A13);
            var now = t0.AddMilliseconds(ms);
            if (AutoAttackState.TryConsumeSwing(player, now, floor, out _))
                accepted.Add(now);
        }
        Assert.NotEmpty(accepted);                                   // the first swing lands
        for (int i = 1; i < accepted.Count; i++)
            Assert.True(accepted[i] - accepted[i - 1] >= floor);     // never two within the floor
    }

    [Fact]
    public void TryConsumeSwing_LegacyOverload_IsUnchanged()
    {
        var player = new PlayerState { SessionId = 0xC0A004 };
        AutoAttackState.Engage(player, 0x00000A13);
        Assert.True(AutoAttackState.TryConsumeSwing(player));
        Assert.False(AutoAttackState.TryConsumeSwing(player));
    }

    [Fact]
    public void AttackRangeTolerance_IsTheBinarysConstant()
        => Assert.Equal(3.890625f, AutoAttackState.AttackRangeTolerance);

    // ---- NPC reach (CB-H9) -----------------------------------------------------------------------

    private static NpcCombatService NewCombat(NpcStateManager npcs)
        => new(npcs, new PlayerStateManager(new NoDirtySink()), null!, null!, null!, null!, null!,
            NullLogger<NpcCombatService>.Instance);

    [Fact]
    public void SelectVictim_UsesTheNpcsReach_NotThePlayersWeaponRange()
    {
        var npcs = NewNpcs();
        var combat = NewCombat(npcs);
        var npc = AddMonster(npcs, 1, hp: 100);
        npc.AttackReach = 3f;
        // The monster's own reach decides, plus the tolerance the player path adds (Finding 2): 3 + 3.890625.
        var reach = npc.AttackReach + AutoAttackState.AttackRangeTolerance;

        // A bow (Auto_Attack_Range 20): the weapon's 20 does not apply — the monster's own reach is used.
        var archer = new PlayerState { MapId = MapA, X = 500f + reach + 2f, Y = 500f, AutoAttackRange = 20 };
        Assert.Null(combat.SelectVictim(npc, archer));

        archer.X = 500f + reach - 0.01f;
        Assert.Same(archer, combat.SelectVictim(npc, archer));
        archer.X = 500f + reach + 0.01f;
        Assert.Null(combat.SelectVictim(npc, archer));
    }

    [Fact]
    public void SelectVictim_IteratingPlayerOnAnotherMap_IsNotSelected()
    {
        var npcs = NewNpcs();
        var combat = NewCombat(npcs);
        var npc = AddMonster(npcs, 1, hp: 100);
        npc.AttackReach = 3f;

        var elsewhere = new PlayerState { MapId = MapB, X = 500f, Y = 500f };   // same xy, other map
        Assert.Null(combat.SelectVictim(npc, elsewhere));
        elsewhere.MapId = MapA;
        Assert.Same(elsewhere, combat.SelectVictim(npc, elsewhere));
    }

    [Fact]
    public void SelectVictim_StateWithNoReach_FallsBackToTheMeleeReach()
    {
        var npcs = NewNpcs();
        var combat = NewCombat(npcs);
        var npc = AddMonster(npcs, 1, hp: 100);
        // AssetIndex 0 has no NPCInfo row, so TrackSpawns takes the fallback: the default melee reach.
        Assert.Equal(NpcCombatService.Reach, npc.AttackReach);

        npc.AttackReach = 0f;   // a state built outside the manager reads as the same fallback
        // Fallback melee reach plus the player-path tolerance (Finding 2): 4 + 3.890625 = 7.890625.
        var reach = NpcCombatService.Reach + AutoAttackState.AttackRangeTolerance;
        var near = new PlayerState { MapId = MapA, X = 500f + reach - 0.01f, Y = 500f };
        var far = new PlayerState { MapId = MapA, X = 500f + reach + 0.01f, Y = 500f };
        Assert.Same(near, combat.SelectVictim(npc, near));
        Assert.Null(combat.SelectVictim(npc, far));
    }

    [Fact]
    public void InNpcReach_NonFinitePosition_IsOutOfReach()
    {
        var npc = new NpcRuntimeState { PosX = 0, PosY = 0, AttackReach = 3f };
        Assert.False(NpcCombatService.InNpcReach(npc, new PlayerState { X = float.NaN, Y = 0 }));
        Assert.False(NpcCombatService.InNpcReach(npc, new PlayerState { X = float.PositiveInfinity, Y = 0 }));
    }
}
