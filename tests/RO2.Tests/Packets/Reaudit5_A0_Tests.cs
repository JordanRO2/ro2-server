using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Tests.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Fifth re-audit, unit A0: the effects a PC's death ends (DeathEffects, R2315), the 2630 threat step sent right before
/// its damage frame and AddAggroRate (NpcCombatService.FlushAggroChangesAsync / ThreatAmount, R2630), and the per-character
/// dungeon reset (DungeonInstanceRegistry.Reset, R1534). Hex strings are expected frames. Shipped tables are found by
/// walking up from the test output directory.
/// </summary>
/// <remarks>In the MountService-state collection: the ride test mounts a PC, and MountService is process-wide and keyed by
/// session id (see RidingServiceTests).</remarks>
[Collection(MountStateCollection.Name)]
public class Reaudit5_A0_Tests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);
    private static readonly Lazy<Task<GameDataStore>> Data = new(LoadDataAsync);

    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    // ============================================================ R2315: the effects a death ends

    [Fact]
    public async Task AfterDeathHandlingZero_IsWhatADeathEnds()
    {
        var data = await Data.Value;
        // ADH 0: the ride, the ranker buff, the map-move buff and the ids of the death burst below.
        foreach (var ended in new uint[] { 100, 50603, 60205, 1117, 1200, 33000, 91340 })
            Assert.True(DeathEffects.IsEndedByDeath(data, ended));
        // ADH 1: the guild buffs, SiegeWeapon, the item buffs 105001/105202 and Aura Armor.
        foreach (var kept in new uint[] { 40000, 40009, 40010, 40011, 40012, 40013, 105001, 105202, 1104 })
            Assert.False(DeathEffects.IsEndedByDeath(data, kept));
        Assert.False(DeathEffects.IsEndedByDeath(data, 999_999_999));   // unknown ids are kept

        var player = new PlayerState();
        foreach (var id in new uint[] { 91340, 40000, 1200, 105202, 1117, 1200 })
            player.ActiveEffects.Add(new ActiveEffect { EffectId = id });
        Assert.Equal(new uint[] { 1117, 1200, 91340 }, DeathEffects.EffectsEndedByDeath(player, data));
    }

    /// <summary>
    /// A PC killed by another PC for 1,470, with the ADH-0 effects 1117, 1200, 33000, 50603 and 91340
    /// and the ADH-1 guild buffs 40000 and 40009-40012 active. After the 2836/2837 the burst is: 2498 2493 2494 for 1117,
    /// 1200, 33000, 2498 2315 2493 2494 for 50603, 2498 2493 2494 for 91340, then the state-6 2398 and the 2392; the
    /// guild buffs stay. Run here as a DoT death (NpcCombatService.RunDotDeathAsync -> SendPlayerDeathAsync) with that
    /// caster and damage, so the 2392 matches byte for byte. The 2493/2494 bodies are compared whole; the 2498s are
    /// this character's own sheet (opcode and length), and the one 2315 (the ranker buff 50603 moves the max HP) is
    /// compared on its msgTag, zero and handle.
    /// </summary>
    [Fact]
    public async Task DotDeath_EndsTheAdh0Effects_AscendingBetweenThe2837AndThe2392_Archive5bb3706a()
    {
        var data = await Data.Value;
        await using var rig = await Rig.StartAsync(data, clients: 1);
        var player = rig.AddPlayer(rig.Sessions[0], 0x07705F11u, hp: 900);
        foreach (var (id, level) in new (uint, byte)[]
                 { (91340, 1), (40000, 1), (50603, 1), (40009, 10), (1117, 1), (40010, 10), (33000, 1), (40011, 10),
                   (1200, 1), (40012, 10) })
            player.ActiveEffects.Add(new ActiveEffect { EffectId = id, EffectLevel = level, TargetHandle = player.EntityId });
        rig.Effects.RefreshPcTraitSheet(player, player.Character!, Array.Empty<InventoryItem>());
        var dot = new ActiveEffect { EffectId = 4108, CasterHandle = 0x07705711u, TickIntervalMs = 1000, HpPerTick = -1470 };

        await rig.TickAsync(player, dot);
        var frames = await rig.FramesUntilMarkerAsync(0);

        var ops = frames.Select(f => f.Op).ToList();
        int deadMe = ops.IndexOf(ZoneS2COpcodes.OnDeadMe);
        Assert.True(deadMe >= 0, string.Join(",", ops));
        Assert.Equal(ZoneS2COpcodes.NfySpinelRevivalInfo, ops[deadMe + 1]);
        Assert.Equal(ZoneS2COpcodes.NfyPCCafeRevivalInfo, ops[deadMe + 2]);

        const string Sheet = "2498", ViewData = "2315";
        var expected = new (ushort Op, string Body)[]
        {
            (ZoneS2COpcodes.OnPcTrait, Sheet),
            (ZoneS2COpcodes.OnDelEffectSuccess, "01141400c8e3000000005d0400000000000000000000"),
            (ZoneS2COpcodes.OnDelEffect, "01181800c9e3000000005d040000115f70070000000000000000"),
            (ZoneS2COpcodes.OnPcTrait, Sheet),
            (ZoneS2COpcodes.OnDelEffectSuccess, "01141400c8e300000000b00400000000000000000000"),
            (ZoneS2COpcodes.OnDelEffect, "01181800c9e300000000b0040000115f70070000000000000000"),
            (ZoneS2COpcodes.OnPcTrait, Sheet),
            (ZoneS2COpcodes.OnDelEffectSuccess, "01141400c8e300000000e88000000000000000000000"),
            (ZoneS2COpcodes.OnDelEffect, "01181800c9e300000000e8800000115f70070000000000000000"),
            (ZoneS2COpcodes.OnPcTrait, Sheet),
            (ZoneS2COpcodes.OnPCViewData, ViewData),
            (ZoneS2COpcodes.OnDelEffectSuccess, "01141400c8e300000000abc500000000000000000000"),
            (ZoneS2COpcodes.OnDelEffect, "01181800c9e300000000abc50000115f70070000000000000000"),
            (ZoneS2COpcodes.OnPcTrait, Sheet),
            (ZoneS2COpcodes.OnDelEffectSuccess, "01141400c8e300000000cc6401000000000000000000"),
            (ZoneS2COpcodes.OnDelEffect, "01181800c9e300000000cc640100115f70070000000000000000"),
            (ZoneS2COpcodes.OnChangeState, "011b1b00eae2115f7007115f7007060000000000000200000000000000"),
            (ZoneS2COpcodes.OnDeadNotify, "01141400cbe200000000115f700711577007be050000"),
        };
        var burst = frames.Skip(deadMe + 3).Take(expected.Length).ToList();
        Assert.True(expected.Length == burst.Count, string.Join(",", burst.Select(f => f.Op)));
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].Op, burst[i].Op);
            if (expected[i].Body == Sheet)
                Assert.Equal(TraitPackets.PcTraitBodyLen * 2, burst[i].Body.Length);
            else if (expected[i].Body == ViewData)
                Assert.StartsWith("97e200000000115f7007", burst[i].Body[(2 + 2 * Convert.FromHexString(burst[i].Body[..2])[0] + 4)..]);
            else
                Assert.Equal(expected[i].Body, burst[i].Body);
        }

        // The ADH-1 guild buffs stay.
        lock (player.ActiveEffects)
            Assert.Equal(new uint[] { 40000, 40009, 40010, 40011, 40012 },
                player.ActiveEffects.Select(e => e.EffectId).Order().ToArray());
    }

    /// <summary>
    /// A fall death (FallDamageHandler, 1565) ends the same effects at the same place: after the 2391 + 2836 + 2837 and
    /// before the second state-6 2398 and the 2392 (killer 0, blob+16 the fall's damage). There is no tracked
    /// ADH-0 effect of its own to replay, so only the placement and the ids are pinned here.
    /// </summary>
    [Fact]
    public async Task FallDeath_EndsTheAdh0Effects_BeforeTheSecondStateSixAndThe2392()
    {
        var data = await Data.Value;
        await using var rig = await Rig.StartAsync(data, clients: 1);
        var player = rig.AddPlayer(rig.Sessions[0], 0x07705F11u, hp: 50);
        foreach (var id in new uint[] { 50603, 40000, 1117 })
            player.ActiveEffects.Add(new ActiveEffect { EffectId = id, EffectLevel = 1, TargetHandle = player.EntityId });
        rig.Effects.RefreshPcTraitSheet(player, player.Character!, Array.Empty<InventoryItem>());

        var fall = new byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(fall, 100f);
        await rig.NewFallHandler().HandleAsync(rig.Sessions[0], fall);
        var frames = await rig.FramesUntilMarkerAsync(0);

        Assert.True(player.IsDead);
        var ops = frames.Select(f => f.Op).ToList();
        int pcCafe = ops.IndexOf(ZoneS2COpcodes.NfyPCCafeRevivalInfo);
        int notify = ops.IndexOf(ZoneS2COpcodes.OnDeadNotify);
        Assert.True(pcCafe > ops.IndexOf(ZoneS2COpcodes.OnDeadMe) && notify > pcCafe, string.Join(",", ops));
        var between = frames.Skip(pcCafe + 1).Take(notify - pcCafe - 1).ToList();
        Assert.Equal(new uint[] { 1117, 50603 }, between.Where(f => f.Op == ZoneS2COpcodes.OnDelEffect)
            .Select(f => BinaryPrimitives.ReadUInt32LittleEndian(Convert.FromHexString(f.Body).AsSpan(1 + 1 + 8))).ToArray());
        Assert.Equal(ZoneS2COpcodes.OnChangeState, between[^1].Op);    // the second state-6 2398 closes the burst
        Assert.Equal(Hex(PlayerDeathPackets.BuildOnChangeStateDead2398(player.EntityId)), between[^1].Body);
        lock (player.ActiveEffects)
            Assert.Equal(new uint[] { 40000 }, player.ActiveEffects.Select(e => e.EffectId).ToArray());
    }

    /// <summary>
    /// The ride (effect 100, ADH 0) ends with the death and is followed by the dismount's 2497 and 2712, as a skill reset
    /// does (TalentResetEffects). The order is this server's own choice; no reference frame exists for a death while
    /// riding.
    /// </summary>
    [Fact]
    public async Task DeathWhileRiding_EndsTheRide_ThenDismounts()
    {
        var data = await Data.Value;
        await using var rig = await Rig.StartAsync(data, clients: 19);   // a session id no other mount test uses
        var session = rig.Sessions[^1];
        MountService.Dismount(session.SessionId);
        var player = rig.AddPlayer(session, 0x0BF86911u, hp: 40);
        try
        {
            await RidingService.MountAsync(session, player, vehicleId: 90230000, skillId: 6003801, rig.Services);
            await rig.FramesUntilMarkerAsync(rig.Sessions.Count - 1);
            var dot = new ActiveEffect { EffectId = 4108, CasterHandle = player.EntityId, TickIntervalMs = 1000, HpPerTick = -92 };

            await rig.TickAsync(player, dot);
            var ops = (await rig.FramesUntilMarkerAsync(rig.Sessions.Count - 1)).Select(f => f.Op).ToList();

            int pcCafe = ops.IndexOf(ZoneS2COpcodes.NfyPCCafeRevivalInfo);
            Assert.Equal(new[]
            {
                ZoneS2COpcodes.OnPcTrait, ZoneS2COpcodes.OnDelEffectSuccess, ZoneS2COpcodes.OnDelEffect,
                ZoneS2COpcodes.OnChangeActorMoveSpeed, ZoneS2COpcodes.NfyDropRidingPet, ZoneS2COpcodes.OnChangeState,
            }, ops.Skip(pcCafe + 1).Take(6).ToArray());
            Assert.False(MountService.IsMounted(session.SessionId));
        }
        finally
        {
            MountService.Dismount(session.SessionId);
        }
    }

    /// <summary>
    /// A tick queued in the same pass as a death does not run once the death removed its effect; an effect that expired in
    /// this pass (it left ActiveEffects before its last tick) still gets its tick.
    /// </summary>
    [Fact]
    public void QueuedTick_OfAnEffectTheDeathRemoved_IsSkipped()
    {
        var player = new PlayerState();
        var stays = new ActiveEffect { EffectId = 105202 };
        var removed = new ActiveEffect { EffectId = 50603 };
        var expiring = new ActiveEffect { EffectId = 4108 };
        player.ActiveEffects.Add(stays);

        Assert.True(EffectService.StillTicks(player, stays, null));
        Assert.False(EffectService.StillTicks(player, removed, null));
        Assert.True(EffectService.StillTicks(player, expiring, [expiring]));
        Assert.False(EffectService.StillTicks(player, new ActiveEffect { EffectId = 105202 }, [expiring]));   // by reference
    }

    // ============================================================ R2630: AddAggroRate and the step before the damage

    /// <summary>
    /// 130510 (Skill_Table_SkillLevel AddAggro 1, AddAggroRate 3): a cast of 64,650 damage steps the threat by 775,804 =
    /// 4 x (3 x 64,650 + 1) under Aura Armor's +30,000; with no stance, 3 x 64,650 + 1. Rate 0 and 1 are both x1
    /// (max(1, rate)), and a normal hit has neither column.
    /// </summary>
    [Fact]
    public void AddAggroRate_MultipliesTheDamage_BeforeTheAddAggro()
    {
        var bytes = File.ReadAllBytes(Path.Combine(RequireDataDirectory(), "Skill_Table_SkillLevel.json"));
        var add = ThreatAmount.Parse(bytes);
        var rates = ThreatAmount.ParseRates(bytes);
        Assert.Equal(1, ThreatAmount.AddAggroOf(add, 130510));
        Assert.Equal(3, ThreatAmount.AddAggroRateOf(rates, 130510));
        Assert.Equal(3, ThreatAmount.AddAggroRateOf(rates, 331210));
        Assert.Equal(2, ThreatAmount.AddAggroRateOf(rates, 331206));
        Assert.Equal(1, ThreatAmount.AddAggroRateOf(rates, 130501));
        Assert.Equal(0, ThreatAmount.AddAggroRateOf(rates, 120201));
        Assert.Equal(ThreatAmount.NormalHitAddAggroRate, ThreatAmount.AddAggroRateOf(rates, 99999999));
        Assert.Equal(103, rates.Count(kv => kv.Value != 0));

        Assert.Equal(193_951L, ThreatAmount.Compute(64_650, 1, 3, 0));
        Assert.Equal(775_804L, ThreatAmount.Compute(64_650, 1, 3, 30_000));
        Assert.Equal(ThreatAmount.Compute(500, 1, 0), ThreatAmount.Compute(500, 1, 1, 0));   // rate 1 = rate 0 = d + 1
        Assert.Equal(501L, ThreatAmount.Compute(500, 1, 0, 0));
        Assert.Equal(1L, ThreatAmount.Compute(-5, 1, 3, 0));                                 // a negative damage counts 0
    }

    /// <summary>
    /// A 1499 through NormalAttackDamageHandler: the onlooker whose open meter (its 1466) is the monster gets the hit's 2630
    /// step BEFORE the 2696, with no NpcCombatService pass run at all (the step is the frame right before the 2696
    /// in its millisecond). The 2630 body is AggroPackets' layout
    /// <c>[u32 monster][u32 target][01 01][01][u32 pc][WStr name][u32 total]</c> with the hit's threat, its damage x1.
    /// </summary>
    [Fact]
    public async Task NormalHit_SendsItsStep_RightBeforeThe2696()
    {
        var data = await Data.Value;
        await using var rig = await Rig.StartAsync(data, clients: 2);
        var attacker = rig.AddPlayer(rig.Sessions[0], 0x070F4011u, hp: 5000);
        var watcher = rig.AddPlayer(rig.Sessions[1], 0x070F4111u, hp: 5000);
        var npc = rig.AddMonster(hp: 1_000_000);
        attacker.X = npc.PosX; attacker.Y = npc.PosY;
        rig.Combat.WatchAggroMeter(watcher, npc.EntityOid);
        AutoAttackState.Engage(attacker, npc.EntityOid);

        var impact = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(impact.AsSpan(4), npc.EntityOid);
        await rig.NewNormalAttackHandler().HandleAsync(rig.Sessions[0], impact);

        var frames = await rig.FramesUntilMarkerAsync(1);
        int damage = npc.MaxHp - npc.Hp;
        Assert.True(damage > 0);
        var ops = frames.Select(f => f.Op).ToList();
        int step = ops.IndexOf(ZoneS2COpcodes.NfyAggroChanged);
        Assert.True(step >= 0, string.Join(",", ops));
        Assert.Equal(ZoneS2COpcodes.NfyAttackDamage, ops[step + 1]);
        Assert.Equal(Hex(AggroPackets.BuildAggroChanged2630(npc.EntityOid, attacker.EntityId, attacker.EntityId,
            attacker.Name, (uint)damage)), frames[step].Body);
        Assert.Single(frames, f => f.Op == ZoneS2COpcodes.NfyAggroChanged);
        // The attacker never opened the meter: no 2630 for it (AggroRecipients).
        Assert.DoesNotContain(await rig.FramesUntilMarkerAsync(0), f => f.Op == ZoneS2COpcodes.NfyAggroChanged);
    }

    /// <summary>
    /// The flush sends a queued step at once, and a killing hit's step never goes out. The killing hit's own step is dropped
    /// because the monster is dead when the handler flushes, right before its damage frame.
    /// </summary>
    [Fact]
    public async Task Flush_SendsAtOnce_AndAKillingHitHasNoStep()
    {
        var data = await Data.Value;
        await using var rig = await Rig.StartAsync(data, clients: 2);
        var attacker = rig.AddPlayer(rig.Sessions[0], 0x070F4011u, hp: 5000);
        var watcher = rig.AddPlayer(rig.Sessions[1], 0x070F4111u, hp: 5000);
        var npc = rig.AddMonster(hp: 100);
        rig.Combat.WatchAggroMeter(watcher, npc.EntityOid);

        rig.Npcs.ApplyDamage(npc.EntityOid, 45, attacker.EntityId, attacker.Name, ThreatAmount.Compute(45, 0, 0));
        await rig.Combat.FlushAggroChangesAsync();
        Assert.Equal(new[] { (ZoneS2COpcodes.NfyAggroChanged,
                Hex(AggroPackets.BuildAggroChanged2630(npc.EntityOid, attacker.EntityId, attacker.EntityId, attacker.Name, 45))) },
            await rig.FramesUntilMarkerAsync(1));

        var (_, died) = rig.Npcs.ApplyDamage(npc.EntityOid, 55, attacker.EntityId, attacker.Name, ThreatAmount.Compute(55, 0, 0));
        Assert.True(died);
        await rig.Combat.FlushAggroChangesAsync();
        Assert.DoesNotContain(await rig.FramesUntilMarkerAsync(1), f => f.Op == ZoneS2COpcodes.NfyAggroChanged);
    }

    /// <summary>
    /// A watcher with the meter open AND the monster selected (its 1307) gets the first hit's acquisition 2643 AFTER the
    /// frame: [2630, 2696, 2643]. Retail never sends the 2643 between the step and the frame.
    /// </summary>
    [Fact]
    public async Task NormalHit_SelectedWatcher_GetsTheAcquisitionAfterTheFrame()
    {
        var data = await Data.Value;
        await using var rig = await Rig.StartAsync(data, clients: 2);
        var attacker = rig.AddPlayer(rig.Sessions[0], 0x070F4011u, hp: 5000);
        var watcher = rig.AddPlayer(rig.Sessions[1], 0x070F4111u, hp: 5000);
        var npc = rig.AddMonster(hp: 1_000_000);
        attacker.X = npc.PosX; attacker.Y = npc.PosY;
        rig.Combat.WatchAggroMeter(watcher, npc.EntityOid);
        await rig.SelectAsync(1, npc.EntityOid);
        Assert.Equal(new[] { ZoneS2COpcodes.AnsActorIconInfo }, (await rig.FramesUntilMarkerAsync(1)).Select(f => f.Op));
        AutoAttackState.Engage(attacker, npc.EntityOid);

        var impact = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(impact.AsSpan(4), npc.EntityOid);
        await rig.NewNormalAttackHandler().HandleAsync(rig.Sessions[0], impact);

        var frames = await rig.FramesUntilMarkerAsync(1);
        var ops = frames.Where(f => f.Op is ZoneS2COpcodes.AnsActorIconInfo or ZoneS2COpcodes.NfyAggroChanged
            or ZoneS2COpcodes.NfyAttackDamage).Select(f => f.Op).ToList();
        Assert.Equal(new[] { ZoneS2COpcodes.NfyAggroChanged, ZoneS2COpcodes.NfyAttackDamage, ZoneS2COpcodes.AnsActorIconInfo }, ops);
        Assert.Equal(Hex(ReqActorDetailInfoHandler.BuildIconOf(npc)),
            frames.Single(f => f.Op == ZoneS2COpcodes.AnsActorIconInfo).Body);
        Assert.Equal(attacker.EntityId, npc.AggroTargetHandle);
    }

    /// <summary>
    /// The 2797 path's pair: <see cref="NpcCombatService.FlushAggroStepsAsync"/> sends only the step, and
    /// <see cref="NpcCombatService.PushPendingTargetChangesAsync"/> (after the frame) the target change, once. For a 2797
    /// retail puts the target-change 2643 after the frame.
    /// </summary>
    [Fact]
    public async Task StepsOnly_ThenThePendingTargetChange()
    {
        var data = await Data.Value;
        await using var rig = await Rig.StartAsync(data, clients: 2);
        var attacker = rig.AddPlayer(rig.Sessions[0], 0x070F4011u, hp: 5000);
        var watcher = rig.AddPlayer(rig.Sessions[1], 0x070F4111u, hp: 5000);
        var npc = rig.AddMonster(hp: 1_000);
        rig.Combat.WatchAggroMeter(watcher, npc.EntityOid);
        await rig.SelectAsync(1, npc.EntityOid);
        await rig.FramesUntilMarkerAsync(1);

        rig.Npcs.ApplyDamage(npc.EntityOid, 45, attacker.EntityId, attacker.Name, ThreatAmount.Compute(45, 0, 0));
        await rig.Combat.FlushAggroStepsAsync();
        Assert.Equal(new[] { ZoneS2COpcodes.NfyAggroChanged }, (await rig.FramesUntilMarkerAsync(1)).Select(f => f.Op));

        await rig.Combat.PushPendingTargetChangesAsync();
        Assert.Equal(new[] { (ZoneS2COpcodes.AnsActorIconInfo, Hex(ReqActorDetailInfoHandler.BuildIconOf(npc))) },
            await rig.FramesUntilMarkerAsync(1));

        await rig.Combat.PushPendingTargetChangesAsync();
        Assert.Empty(await rig.FramesUntilMarkerAsync(1));
    }

    // ============================================================ R1534: each member's reset releases its own binding

    /// <summary>
    /// Two party members entered DungeonID 64; the first member's 1534 gets 2771 [64], and the second member's later 1534
    /// gets 2771 [64] too. The second answer is what the per-character release gives; closing the party's instance on
    /// the first reset left it silent.
    /// </summary>
    [Fact]
    public void TwoMembersResets_AreBothAnswered_Live0927_1e8df2ee()
    {
        const uint first = 8425599, second = 8509175, world = 1221, instanceMap = 426101;
        var registry = new DungeonInstanceRegistry();
        var owner = DungeonInstanceOwner.For(new PartyState { PartyId = 751893 }, first);
        var entry = registry.Enter(owner, 64, instanceMap, first);
        Assert.True(registry.Enter(owner, 64, instanceMap, second).CountsForCharacter);

        Assert.Equal(new[] { "40000000" }, registry.Reset(owner, world, first).Select(id => Hex(DungeonResetHandler.Build2771(id))));
        Assert.True(registry.IsOpen(entry.InstanceId));
        Assert.Equal(new[] { "40000000" }, registry.Reset(owner, world, second).Select(id => Hex(DungeonResetHandler.Build2771(id))));
        Assert.False(registry.IsOpen(entry.InstanceId));
    }

    /// <summary>
    /// A burst (the first 1534 answered [64], the next two silent): the first frame releases the
    /// requester's binding, so the rest of its burst finds nothing, while the member who has not reset keeps the instance
    /// open, and the released character's next entry into it counts again (the model's consequence, see
    /// DungeonInstanceRegistry.Reset). A party member never counted in an instance (it joined the party later) is not
    /// answered for it.
    /// </summary>
    [Fact]
    public void Burst_FirstFrameAnswered_RestSilent_AndTheReleasedMemberCountsAgain()
    {
        const uint leader = 0xC9F4_0001u, member = 0xC9F4_0002u, late = 0xC9F4_0003u, world = 1221, instanceMap = 426101;
        var registry = new DungeonInstanceRegistry();
        var owner = DungeonInstanceOwner.For(new PartyState { PartyId = 0xC9F4 }, leader);
        var entry = registry.Enter(owner, 64, instanceMap, leader);
        registry.Enter(owner, 64, instanceMap, member);

        Assert.Equal(new byte[] { 64 }, registry.Reset(owner, world, member));
        Assert.Empty(registry.Reset(owner, world, member));
        Assert.Empty(registry.Reset(owner, world, member));
        Assert.Empty(registry.Reset(owner, world, late));
        Assert.Empty(registry.Reset(owner, instanceMap, leader));            // from inside: silent, nothing changes
        Assert.True(registry.IsBound(entry.InstanceId, leader));
        Assert.False(registry.IsBound(entry.InstanceId, member));

        Assert.True(registry.WouldCount(owner, 64, member));
        Assert.Equal(new DungeonInstanceEntry(false, true, entry.InstanceId), registry.Enter(owner, 64, instanceMap, member));
    }

    /// <summary>A finder run ends with its character's own 1534 even while a party member keeps the instance open
    /// (DungeonFinderPendingStore.GetRun asks IsBound). Uses the shared registry, with character ids no other test uses.</summary>
    [Fact]
    public void FinderRun_EndsWithTheMembersOwnReset_WhileTheInstanceStaysOpen()
    {
        const uint a = 0x5A50_0001u, b = 0x5A50_0002u;
        var owner = DungeonInstanceOwner.For(new PartyState { PartyId = 0x5A50_0000u }, a);
        var entry = DungeonInstanceRegistry.Shared.EnterFinder(owner, 63, 410104, a);
        DungeonInstanceRegistry.Shared.Enter(owner, 63, 410104, b);
        var player = new PlayerState { CharacterId = a };
        var run = new DungeonFinderPendingStore.FinderRun(21, 410104, entry.InstanceId);
        DungeonFinderPendingStore.SetRun(player, run);
        try
        {
            Assert.Same(run, DungeonFinderPendingStore.GetRun(player));
            Assert.Equal(new byte[] { 63 }, DungeonInstanceRegistry.Shared.Reset(owner, 1100, a));
            Assert.True(DungeonInstanceRegistry.Shared.IsOpen(entry.InstanceId));
            Assert.Null(DungeonFinderPendingStore.GetRun(player));
        }
        finally
        {
            DungeonInstanceRegistry.Shared.Reset(owner, 1100, a);
            DungeonInstanceRegistry.Shared.Reset(owner, 1100, b);
        }
        Assert.False(DungeonInstanceRegistry.Shared.IsOpen(entry.InstanceId));
    }

    // ============================================================ shared

    private sealed class NoDirtySink : IDirtySink
    {
        public void Mark(PlayerState player) { }
        public void Forget(uint characterId) { }
    }

    private static async Task<GameDataStore> LoadDataAsync()
    {
        var store = new GameDataStore();
        await store.LoadAsync(RequireDataDirectory());
        return store;
    }

    /// <summary>Walks up from the test output path to the repository's data directory.</summary>
    private static string RequireDataDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "data");
            if (File.Exists(Path.Combine(candidate, "Effect_Table.json")) && File.Exists(Path.Combine(candidate, "Skill_Table_SkillLevel.json")))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("no ancestor of " + AppContext.BaseDirectory + " holds data/Effect_Table.json");
    }

    /// <summary>An empty bag with nothing worn.</summary>
    private sealed class NoGearInventory : IInventoryRepository
    {
        public Task<IReadOnlyList<InventoryItem>> GetByCharacterAsync(uint characterId) =>
            Task.FromResult<IReadOnlyList<InventoryItem>>(Array.Empty<InventoryItem>());
        public Task<IReadOnlyList<InventoryItem>> GetEquipmentAsync(uint characterId) =>
            Task.FromResult<IReadOnlyList<InventoryItem>>(Array.Empty<InventoryItem>());
        public Task<InventoryItem?> GetByIdAsync(ulong id) => Task.FromResult<InventoryItem?>(null);
        public Task AddItemAsync(InventoryItem item) => Task.CompletedTask;
        public Task RemoveItemAsync(ulong itemId) => Task.CompletedTask;
        public Task UpdateItemAsync(InventoryItem item) => Task.CompletedTask;
        public Task<ulong> GetNextIdAsync() => Task.FromResult(1UL);
        public Task<int> RepairSlotsAsync(uint characterId) => Task.FromResult(0);
    }

    /// <summary>
    /// <c>clients</c> real <see cref="ProudSession"/>s on a loopback <see cref="ProudServer"/>, read from the
    /// client end, with the singletons the death, the hit and the fall resolve (GameDataStore, BroadcastService,
    /// NpcStateManager, an empty inventory, EffectService, DurabilityService, NpcChatService, NpcCombatService,
    /// CombatService) and no database.
    /// </summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly ProudServer _server;
        private readonly List<Socket> _clients;
        private readonly ServiceProvider _services;
        private readonly BroadcastService _broadcast;

        public List<ProudSession> Sessions { get; }
        public PlayerStateManager Players { get; }
        public NpcStateManager Npcs { get; }
        public EffectService Effects { get; }
        public NpcCombatService Combat { get; }
        public IServiceProvider Services => _services;

        private Rig(ProudServer server, List<Socket> clients, List<ProudSession> sessions, GameDataStore data)
        {
            _server = server;
            _clients = clients;
            Sessions = sessions;
            Players = new PlayerStateManager(new NoDirtySink());
            Npcs = new NpcStateManager(data, NullLogger<NpcStateManager>.Instance);
            var broadcast = new BroadcastService(Players, NullLogger<BroadcastService>.Instance);
            broadcast.SetServer(server);
            _broadcast = broadcast;
            var players = Players;
            var npcs = Npcs;
            var collection = new ServiceCollection();
            collection.AddSingleton(data);
            collection.AddSingleton(broadcast);
            collection.AddSingleton(npcs);
            collection.AddSingleton(players);
            collection.AddSingleton<IInventoryRepository>(new NoGearInventory());
            collection.AddSingleton(sp => new EffectService(players, broadcast, data, npcState: npcs, loot: null!,
                sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<EffectService>.Instance, emblems: TestEmblems.Create()));
            collection.AddSingleton(sp => new DurabilityService(data, NullLogger<DurabilityService>.Instance,
                sp.GetRequiredService<IServiceScopeFactory>(), broadcast));
            collection.AddSingleton(_ => new NpcChatService(data, npcs, players, broadcast, NullLogger<NpcChatService>.Instance));
            collection.AddSingleton(sp => new NpcCombatService(npcs, players, broadcast,
                sp.GetRequiredService<DurabilityService>(), sp.GetRequiredService<NpcChatService>(),
                new PcCombatStateService(players, broadcast, NullLogger<PcCombatStateService>.Instance),
                sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<NpcCombatService>.Instance));
            collection.AddSingleton(sp => new CombatService(players, npcs, broadcast, data,
                NullLogger<CombatService>.Instance, sp.GetRequiredService<IServiceScopeFactory>()));
            _services = collection.BuildServiceProvider();
            Effects = _services.GetRequiredService<EffectService>();
            Combat = _services.GetRequiredService<NpcCombatService>();
        }

        public static async Task<Rig> StartAsync(GameDataStore data, int clients)
        {
            var server = new ProudServer("test", NullLogger.Instance);
            var sessions = new List<ProudSession>();
            var connected = new SemaphoreSlim(0);
            server.SessionConnected += s =>
            {
                lock (sessions) sessions.Add(s);
                connected.Release();
                return Task.CompletedTask;
            };
            await server.StartAsync(new IPEndPoint(IPAddress.Loopback, 0));
            var sockets = new List<Socket>();
            for (int i = 0; i < clients; i++)
            {
                var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                await client.ConnectAsync(server.LocalEndPoint!);
                Assert.True(await connected.WaitAsync(Generous));
                sockets.Add(client);
            }
            List<ProudSession> ordered;
            lock (sessions) ordered = sessions.OrderBy(s => s.SessionId).ToList();
            return new Rig(server, sockets, ordered, data);
        }

        /// <summary>An in-world PC on map 1100 on <paramref name="session"/>, registered for the broadcasts.</summary>
        public PlayerState AddPlayer(ProudSession session, uint entityId, int hp)
        {
            var player = new PlayerState
            {
                SessionId = session.SessionId, EntityId = entityId, CharacterId = 915_700 + session.SessionId,
                Name = "Pc" + session.SessionId, Level = 30, MapId = 1100, MapLoaded = true, Hp = hp, MaxHp = 5000,
                Mp = 300, MaxMp = 500, BindIndex = 13, X = 500f, Y = 500f,
                Character = new Character { Id = 915_700 + session.SessionId, Name = "Pc" + session.SessionId, Job = 1, Level = 30,
                    Gender = 1, Race = 1, Str = 60, Agi = 30, Vit = 80, Int = 20, Wis = 20 },
            };
            Players.AddPlayer(session.SessionId, player);
            return player;
        }

        /// <summary>A hostile monster on map 1100, tracked by the NpcStateManager.</summary>
        public NpcRuntimeState AddMonster(int hp)
        {
            var spawn = new SpawnEntry { MapId = 1100, Index = 9_501, AssetIndex = 0, PosX = 500f, PosY = 500f };
            Npcs.TrackSpawns(1100, [spawn]);
            var npc = Npcs.GetAliveNpcs(1100).Single();
            npc.Camp = 2;
            npc.Level = 1;
            npc.MaxHp = hp;
            npc.Hp = hp;
            return npc;
        }

        /// <summary>One periodic tick of <paramref name="effect"/> on <paramref name="player"/> (EffectService's private
        /// TickPlayerEffectAsync, which its timer loop calls), awaited.</summary>
        public Task TickAsync(PlayerState player, ActiveEffect effect)
        {
            var tick = typeof(EffectService).GetMethod("TickPlayerEffectAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            return (Task)tick.Invoke(Effects, new object[] { player, effect })!;
        }

        public FallDamageHandler NewFallHandler() => new(_services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<FallDamageHandler>.Instance, Players, _broadcast);

        /// <summary>Client <paramref name="index"/> selects <paramref name="actorHandle"/> with a 1307 through
        /// ReqActorDetailInfoHandler (body 14: compact width marker 1, blob length 12, the handle at body+10).</summary>
        public Task SelectAsync(int index, uint actorHandle)
        {
            var body = new byte[14];
            body[0] = 1;
            body[1] = 12;
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(10), actorHandle);
            return new ReqActorDetailInfoHandler(_services.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<ReqActorDetailInfoHandler>.Instance, Players, _broadcast).HandleAsync(Sessions[index], body);
        }

        public NormalAttackDamageHandler NewNormalAttackHandler() => new(_services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<NormalAttackDamageHandler>.Instance, Players, _broadcast);

        private async Task<(ushort Op, string Body)> NextRmiHexAsync(Socket client)
        {
            while (true)
            {
                var head = await ReadExactAsync(client, 3);
                Assert.Equal(0x13, head[0]);
                Assert.Equal(0x57, head[1]);
                var lengthBytes = await ReadExactAsync(client, head[2]);
                int length = head[2] switch
                {
                    1 => lengthBytes[0],
                    2 => BinaryPrimitives.ReadUInt16LittleEndian(lengthBytes),
                    _ => BinaryPrimitives.ReadInt32LittleEndian(lengthBytes),
                };
                var data = await ReadExactAsync(client, length);
                if (data.Length >= 3 && data[0] == 0x01)
                    return (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(1)),
                        Convert.ToHexString(data, 3, data.Length - 3).ToLowerInvariant());
            }
        }

        /// <summary>Sends a marker frame (opcode 0, body <c>7e57</c>) to client <paramref name="index"/> and returns, in
        /// order, every RMI frame it receives before it.</summary>
        public async Task<List<(ushort Op, string Body)>> FramesUntilMarkerAsync(int index)
        {
            await Sessions[index].SendInternalRmiAsync(0, new byte[] { 0x7E, 0x57 });
            var frames = new List<(ushort Op, string Body)>();
            while (true)
            {
                var frame = await NextRmiHexAsync(_clients[index]);
                if (frame == (0, "7e57")) return frames;
                frames.Add(frame);
            }
        }

        private static async Task<byte[]> ReadExactAsync(Socket client, int count)
        {
            var buffer = new byte[count];
            int read = 0;
            while (read < count)
            {
                int n = await client.ReceiveAsync(buffer.AsMemory(read), SocketFlags.None).AsTask().WaitAsync(Generous);
                if (n == 0) throw new EndOfStreamException("connection closed mid-frame");
                read += n;
            }
            return buffer;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var c in _clients) c.Dispose();
            await _services.DisposeAsync();
            await _server.StopAsync();
        }
    }
}
