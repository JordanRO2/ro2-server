using System.Buffers.Binary;
using RO2.Skills.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Domain.Entities;
using RO2.GameData;
using RO2.Network.Protocol;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;
using static RO2.Tests.Packets.ReauditShared;
using RO2.Inventory.Domain;

namespace RO2.Tests.Packets;

/// <summary>
/// HSV combat-skills cast path, cluster C: the cast level the client names (CB-H1/CB-G2), the 1508 start
/// preconditions and RecentSkillStart bookkeeping (CB-H4), the 1549 tick ledger (CB-H3), the 1506/1507 echo (CB-H7)
/// and the 1561 item-skill ownership (CB-H10). Oracle throughout: the exploit is refused — a gate result, a silent
/// 2715 (20152) with the pending-cast count unchanged, no broadcast frame, or an item left unconsumed.
/// </summary>
public class CastPathHardeningTests
{
    private const uint Map = 1100;
    private const uint TalentGroup = 3124;       // a Talent_Table group, Max_Skill_Level 5
    private const uint L1 = 312401;
    private const uint L5 = 312405;
    private const uint L10 = 312410;

    private static GateActor Actor(byte learnedLevel, bool dead = false)
        => new(Map, 0, 0, dead, new Dictionary<uint, byte> { [TalentGroup] = learnedLevel });

    // ---- CB-H1 / CB-G2: the cast level --------------------------------------------------------------------------------

    [Fact]
    public void Gate_LearnedLevel1_SentAsLevel10_IsRefused_ForGroundAndNpcCasts()
    {
        var npc = new GateTarget(Map, 3, 0, false);
        Assert.Equal(ActorRefusal.SkillNotLearned, SkillDamageHandler.EvaluateDamageGate(Actor(1), L10, null, 10f));
        Assert.Equal(ActorRefusal.SkillNotLearned, SkillDamageHandler.EvaluateDamageGate(Actor(1), L10, npc, 10f));
        // the learned level itself, and a lower one, pass
        Assert.Equal(ActorRefusal.None, SkillDamageHandler.EvaluateDamageGate(Actor(1), L1, npc, 10f));
        Assert.Equal(ActorRefusal.None, SkillDamageHandler.EvaluateDamageGate(Actor(5), L1, null, 10f));
        Assert.Equal(ActorRefusal.None, SkillDamageHandler.EvaluateDamageGate(Actor(5), L5, npc, 10f));
    }

    [Fact]
    public void Gate_LevelAboveTheTableMax_OrLevelZero_IsRefused()
    {
        // learned 10 but the group's Max_Skill_Level is 5
        Assert.Equal(ActorRefusal.SkillNotLearned,
            SkillDamageHandler.EvaluateDamageGate(Actor(10), L10, null, 10f, maxSkillLevel: 5));
        Assert.Equal(ActorRefusal.None,
            SkillDamageHandler.EvaluateDamageGate(Actor(10), L5, null, 10f, maxSkillLevel: 5));
        // level 0 (id 312400) is no cast level
        Assert.Equal(ActorRefusal.SkillNotLearned, SkillDamageHandler.EvaluateDamageGate(Actor(5), 312400, null, 10f));
    }

    [Fact]
    public void Gate_DoesNotRaiseTheLevelRule_ForItemSkills_AndKeepsTheEarlierRefusals()
    {
        var none = new GateActor(Map, 0, 0, false, new Dictionary<uint, byte>());
        // an item skill (>= 2M) is never in LearnedSkills and keeps passing on possession (the pending cast)
        Assert.Equal(ActorRefusal.None, SkillDamageHandler.EvaluateDamageGate(none, 9201001, null, 10f));
        // the earlier refusals keep their precedence over the level rule
        var far = new GateTarget(Map, 500, 0, false);
        Assert.Equal(ActorRefusal.OutOfRange, SkillDamageHandler.EvaluateDamageGate(Actor(1), L10, far, 10f));
        Assert.Equal(ActorRefusal.ActorDead, SkillDamageHandler.EvaluateDamageGate(Actor(1, dead: true), L10,
            new GateTarget(Map, 1, 0, false), 10f));
    }

    [Fact]
    public async Task TryGetSkillLevelLimit_ReadsTheTalentTable_AndClampCastLevelUsesIt()
    {
        var store = await LoadStoreAsync("Talent_Table.json");
        Assert.True(TalentRules.TryGetSkillLevelLimit(store, TalentGroup, out var max));
        Assert.Equal(5, max);
        Assert.False(TalentRules.TryGetSkillLevelLimit(store, 92_016u, out var none));   // no Talent_Table row
        Assert.Equal(0, none);
        Assert.False(TalentRules.TryGetSkillLevelLimit(store, 9_000_211u, out _));        // beyond ushort

        // learned 1, id says 10 -> 1; learned 10 -> table max 5; never learned -> 0 (no skill rate)
        PlayerState Hero(byte learned) => new() { LearnedSkills = new Dictionary<uint, byte> { [TalentGroup] = learned } };
        Assert.Equal(1, CombatService.ClampCastLevel(store, Hero(1), L10));
        Assert.Equal(5, CombatService.ClampCastLevel(store, Hero(10), L10));
        Assert.Equal(3, CombatService.ClampCastLevel(store, Hero(10), 312403));
        Assert.Equal(0, CombatService.ClampCastLevel(store, new PlayerState(), L10));
        // an item / guild / monster skill has no learned entry and no talent row: its own level stands
        Assert.Equal(11, CombatService.ClampCastLevel(store, new PlayerState(), 9_000_211u));
    }

    // ---- CB-H4: 1508 ReqSkillStart ------------------------------------------------------------------------------------

    private static byte[] Request1508(uint skillId, uint target = 0x0ADEAD11u)
    {
        var req = new byte[47];
        BinaryPrimitives.WriteUInt32LittleEndian(req, skillId);
        BinaryPrimitives.WriteUInt32LittleEndian(req.AsSpan(16), target);
        BinaryPrimitives.WriteUInt16LittleEndian(req.AsSpan(45), 1);
        return req;
    }

    private sealed class Rig : IAsyncDisposable
    {
        public required Harness Net { get; init; }
        public required PlayerStateManager Players { get; init; }
        public required BroadcastService Broadcast { get; init; }
        public required PlayerState Caster { get; init; }
        public required ServiceProvider Services { get; init; }
        public IServiceScopeFactory Scopes => Services.GetRequiredService<IServiceScopeFactory>();

        public static async Task<Rig> StartAsync(int sessions, GameDataStore store,
            Action<ServiceCollection>? configure = null, Action<PlayerState>? tweak = null)
        {
            var net = await Harness.StartAsync(sessions);
            var players = new PlayerStateManager(new NoDirtySink());
            var broadcast = new BroadcastService(players, NullLogger<BroadcastService>.Instance);
            broadcast.SetServer(net.Server);
            PlayerState? first = null;
            for (var i = 0; i < sessions; i++)
            {
                var sid = net.Sessions[i].SessionId;
                var p = new PlayerState { SessionId = sid, EntityId = (sid << 8) | 0x11, CharacterId = 4242 + (uint)i,
                    MapId = Map, MapLoaded = true, Level = 10, Hp = 500, MaxHp = 500, Mp = 100, MaxMp = 100 };
                players.AddPlayer(sid, p);
                first ??= p;
            }
            tweak?.Invoke(first!);
            var collection = new ServiceCollection();
            collection.AddSingleton(store);
            configure?.Invoke(collection);
            return new Rig { Net = net, Players = players, Broadcast = broadcast, Caster = first!,
                Services = collection.BuildServiceProvider() };
        }

        /// <summary>Sends a marker frame; reading it back first proves nothing else was queued before it.</summary>
        public async Task AssertNothingQueuedAsync(int session = 0)
        {
            await Net.Sessions[session].SendInternalRmiAsync(ZoneS2COpcodes.OnLevelUp, new byte[] { 0x5A });
            Assert.Equal((ZoneS2COpcodes.OnLevelUp, "5a"), Hexed(await Net.Readers[session].NextAsync()));
        }

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            await Net.DisposeAsync();
        }
    }

    private static SkillStartHandler StartHandler(Rig rig, Func<DateTime>? clock = null)
        => new(rig.Scopes, NullLogger<SkillStartHandler>.Instance, rig.Players, rig.Broadcast)
        {
            Clock = clock ?? (static () => DateTime.UtcNow),
        };

    private async Task AssertSilentRefusal2715(Rig rig)
    {
        var refusal = await rig.Net.Readers[0].NextAsync();
        Assert.Equal(ZoneS2COpcodes.AnsUseSkillStart, refusal.Op);
        Assert.Equal(SkillPackets.SkillResultOnCooldown, BinaryPrimitives.ReadUInt16LittleEndian(refusal.Body));
        await rig.AssertNothingQueuedAsync();   // no 2716, no 2962
    }

    [Fact]
    public async Task SkillStart_DeadCaster_GetsTheSilent2715_NoPendingCast_NoSpSpent()
    {
        var store = await LoadStoreAsync("Talent_Table.json", "Skill_Table_SkillLevel.json");
        await using var rig = await Rig.StartAsync(1, store, tweak: p =>
        {
            p.IsDead = true;
            p.LearnedSkills[TalentGroup] = 5;
        });
        await StartHandler(rig).HandleAsync(rig.Net.Sessions[0], Request1508(L5));

        await AssertSilentRefusal2715(rig);
        Assert.Equal(0, SkillCastLandings.PendingCount(rig.Caster, DateTime.UtcNow));
        Assert.Equal(100, rig.Caster.Mp);
        Assert.Empty(rig.Caster.RecentSkillStart);
        Assert.Empty(rig.Caster.SkillCooldowns);
    }

    [Fact]
    public async Task SkillStart_TalentSkill_NotLearned_OrWithNoTableRow_IsRefused_NothingRecorded()
    {
        var store = await LoadStoreAsync("Talent_Table.json", "Skill_Table_SkillLevel.json");
        await using var rig = await Rig.StartAsync(1, store, tweak: p => p.LearnedSkills[TalentGroup] = 5);
        var handler = StartHandler(rig);

        // 312499: learned group, but Skill_Table_SkillLevel has no such level -> no table row
        Assert.False(store.SkillLevels.ContainsKey(312499));
        await handler.HandleAsync(rig.Net.Sessions[0], Request1508(312499));
        await AssertSilentRefusal2715(rig);

        // a talent group the caster never learned
        rig.Caster.LearnedSkills.Remove(TalentGroup);
        await handler.HandleAsync(rig.Net.Sessions[0], Request1508(L5));
        await AssertSilentRefusal2715(rig);

        Assert.Equal(0, SkillCastLandings.PendingCount(rig.Caster, DateTime.UtcNow));
        Assert.Equal(100, rig.Caster.Mp);
        Assert.Empty(rig.Caster.RecentSkillStart);
    }

    [Fact]
    public async Task SkillStart_LearnedTalentSkill_IsStillAccepted_AndRecordsRecentSkillStartOnlyWhenAccepted()
    {
        var store = await LoadStoreAsync("Talent_Table.json", "Skill_Table_SkillLevel.json");
        await using var rig = await Rig.StartAsync(1, store, tweak: p => p.LearnedSkills[TalentGroup] = 5);
        var t0 = new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);
        var now = t0;
        var handler = StartHandler(rig, () => now);

        await handler.HandleAsync(rig.Net.Sessions[0], Request1508(L5));
        var accept = await rig.Net.Readers[0].NextAsync();
        Assert.Equal(ZoneS2COpcodes.AnsUseSkillStart, accept.Op);
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(accept.Body));
        Assert.Equal(ZoneS2COpcodes.NfyUseSkillStart, (await rig.Net.Readers[0].NextAsync()).Op);
        Assert.Equal(84, rig.Caster.Mp);   // Use_SP 16, spent once
        Assert.Equal(1, SkillCastLandings.PendingCount(rig.Caster, t0));
        Assert.Equal(t0, rig.Caster.RecentSkillStart[TalentGroup]);

        // A burst copy 300 ms later is silent (cooldown running, cast not landed) and must NOT refresh the stamp:
        // before the fix the refused frame wrote it, so a held key stayed a "burst duplicate" for good.
        now = t0.AddMilliseconds(300);
        await handler.HandleAsync(rig.Net.Sessions[0], Request1508(L5));
        await rig.AssertNothingQueuedAsync();
        Assert.Equal(t0, rig.Caster.RecentSkillStart[TalentGroup]);
        Assert.Equal(84, rig.Caster.Mp);

        // And so the next copy, 400 ms after the ACCEPTED frame (700 ms in), is no longer a burst duplicate: it is the
        // plain in-cooldown refusal (a 2715 + 2962), again leaving the stamp alone.
        now = t0.AddMilliseconds(700);
        await handler.HandleAsync(rig.Net.Sessions[0], Request1508(L5));
        var refused = await rig.Net.Readers[0].NextAsync();
        Assert.Equal(ZoneS2COpcodes.AnsUseSkillStart, refused.Op);
        Assert.NotEqual(0, BinaryPrimitives.ReadUInt16LittleEndian(refused.Body));
        Assert.Equal(ZoneS2COpcodes.NfySkillStartError, (await rig.Net.Readers[0].NextAsync()).Op);
        Assert.Equal(t0, rig.Caster.RecentSkillStart[TalentGroup]);
    }

    // ---- CB-H3: the 1549 tick ledger ----------------------------------------------------------------------------------

    [Fact]
    public void TickLedger_ExactReplayOfTheLastSequence_IsRefused_DecreasingIsAllowed()
    {
        var p = new PlayerState();
        var t = new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(SkillCastLandings.TryAcceptTick(p, L5, 7, t));
        // an exact replay of the last accepted sequence, past the interval floor, is refused
        Assert.False(SkillCastLandings.TryAcceptTick(p, L5, 7, t.AddSeconds(1)));
        // the 1549 sequence byte is not a proven per-cast counter, so a LOWER sequence is NOT a violation:
        // it lands, gated only by the interval floor and the per-cast cap (a refused tick updates nothing)
        Assert.True(SkillCastLandings.TryAcceptTick(p, L5, 3, t.AddSeconds(2)));
        Assert.True(SkillCastLandings.TryAcceptTick(p, L5, 8, t.AddSeconds(3)));
        Assert.True(SkillCastLandings.TryAcceptTick(p, L5, 9, t.AddSeconds(4)));
    }

    [Fact]
    public void TickLedger_BurstInsideTheIntervalFloor_LandsOnce()
    {
        var p = new PlayerState();
        var t = new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);
        var accepted = 0;
        for (byte seq = 1; seq <= 20; seq++)   // 20 ticks inside 10 ms
            if (SkillCastLandings.TryAcceptTick(p, L5, seq, t.AddMilliseconds(seq / 2.0))) accepted++;
        Assert.Equal(1, accepted);
        Assert.True(SkillCastLandings.TryAcceptTick(p, L5, 21, t.AddMilliseconds(10) + SkillCastLandings.MinTickInterval));
    }

    [Fact]
    public void TickLedger_CapsTheCount_AndRestartsWithAFreshCast()
    {
        var p = new PlayerState();
        var t = new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);
        var accepted = 0;
        for (var i = 0; i < 200; i++)
            if (SkillCastLandings.TryAcceptTick(p, L5, (byte)(i + 1), t.AddMilliseconds(i * 150))) accepted++;
        Assert.Equal(SkillCastLandings.MaxTicksPerCast, accepted);

        // a fresh accepted 1509 (ResetTicks) / a new accepted cast (RecordPending) starts the run over
        SkillCastLandings.ResetTicks(p, L5);
        Assert.True(SkillCastLandings.TryAcceptTick(p, L5, 1, t.AddSeconds(60)));
        SkillCastLandings.RecordPending(p, L5, 0, t.AddSeconds(61));
        Assert.True(SkillCastLandings.TryAcceptTick(p, L5, 1, t.AddSeconds(62)));
        // the ledgers are per skill id
        Assert.True(SkillCastLandings.TryAcceptTick(p, 412105, 1, t.AddSeconds(62)));
    }

    [Fact]
    public void TickLedger_EndsWithTheCastItBelongsTo()
    {
        var p = new PlayerState();
        var t = new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);
        SkillCastLandings.RecordPending(p, L5, 0, t);
        Assert.True(SkillCastLandings.TryAcceptTick(p, L5, 5, t.AddSeconds(1)));
        Assert.True(SkillCastLandings.TryGovern(p, L5, t.AddSeconds(2), consume: true, out _));   // the cast ends
        Assert.True(SkillCastLandings.TryAcceptTick(p, L5, 1, t.AddSeconds(3)));
    }

    // ---- CB-H7: 1506 ReqSkillCastingStart / 1507 ReqSkillCastingCancel -----------------------------------------------

    private static byte[] Request1506(uint skillId, float x, float y, float z, ulong castId = 0xC0FFEEu)
    {
        var req = new byte[43];
        BinaryPrimitives.WriteUInt32LittleEndian(req, skillId);
        BinaryPrimitives.WriteUInt64LittleEndian(req.AsSpan(4), castId);
        BinaryPrimitives.WriteSingleLittleEndian(req.AsSpan(17), x);
        BinaryPrimitives.WriteSingleLittleEndian(req.AsSpan(21), y);
        BinaryPrimitives.WriteSingleLittleEndian(req.AsSpan(25), z);
        return req;
    }

    private static SkillCastingStartHandler CastingStartHandler(Rig rig)
        => new(rig.Scopes, NullLogger<SkillCastingStartHandler>.Instance, rig.Players, rig.Broadcast);

    [Fact]
    public async Task CastingStart_NonFinitePosition_OrUnknownSkill_BroadcastsNothing()
    {
        var store = await LoadStoreAsync("Skill_Table_SkillLevel.json");
        await using var rig = await Rig.StartAsync(2, store);
        var handler = CastingStartHandler(rig);

        await handler.HandleAsync(rig.Net.Sessions[0], Request1506(L5, float.NaN, 0f, 0f));
        await handler.HandleAsync(rig.Net.Sessions[0], Request1506(L5, 0f, float.PositiveInfinity, 0f));
        await handler.HandleAsync(rig.Net.Sessions[0], Request1506(0xDEADBEEFu, 1f, 2f, 3f));
        await rig.AssertNothingQueuedAsync(0);   // no 2709, no 2708 for the caster
        await rig.AssertNothingQueuedAsync(1);   // and no 2709 for anybody else on the map
        Assert.False(SkillCastLandings.IsCasting(rig.Caster, L5, DateTime.UtcNow));
    }

    [Fact]
    public async Task CastingStart_DeadOrUnloadedCaster_BroadcastsNothing()
    {
        var store = await LoadStoreAsync("Skill_Table_SkillLevel.json");
        await using var rig = await Rig.StartAsync(2, store);
        var handler = CastingStartHandler(rig);

        rig.Caster.IsDead = true;
        await handler.HandleAsync(rig.Net.Sessions[0], Request1506(L5, 1f, 2f, 3f));
        rig.Caster.IsDead = false;
        rig.Caster.MapLoaded = false;
        await handler.HandleAsync(rig.Net.Sessions[0], Request1506(L5, 1f, 2f, 3f));
        await rig.AssertNothingQueuedAsync(0);
        await rig.AssertNothingQueuedAsync(1);
    }

    [Fact]
    public async Task CastingStart_ValidRequest_IsBroadcast_WithTheFarPositionClamped()
    {
        var store = await LoadStoreAsync("Skill_Table_SkillLevel.json");
        await using var rig = await Rig.StartAsync(2, store);
        await CastingStartHandler(rig).HandleAsync(rig.Net.Sessions[0], Request1506(L5, 5e9f, -5e9f, 3f));

        var seconds = store.SkillLevels[L5].Casting_Time;
        var expected = SkillDamagePackets.BuildSkillCastingStart2709(rig.Caster.EntityId, L5, 0xC0FFEEu, seconds, 0u,
            SkillCastingStartHandler.MaxCastCoordinate, -SkillCastingStartHandler.MaxCastCoordinate, 3f, 0);
        foreach (var session in new[] { 0, 1 })
            Assert.Equal((ZoneS2COpcodes.NfySkillCastingStart, Hex(expected)), Hexed(await rig.Net.Readers[session].NextAsync()));
        Assert.Equal(ZoneS2COpcodes.AnsSkillCastingStart, (await rig.Net.Readers[0].NextAsync()).Op);
        Assert.True(SkillCastLandings.IsCasting(rig.Caster, L5, DateTime.UtcNow));
    }

    private static SkillCastingCancelHandler CancelHandler(Rig rig)
        => new(rig.Scopes, NullLogger<SkillCastingCancelHandler>.Instance, rig.Players, rig.Broadcast);

    private static byte[] Request1507(uint skillId)
    {
        var req = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(req, skillId);
        return req;
    }

    [Fact]
    public async Task CastingCancel_WithNoOpenCast_AnswersTheCaster_ButBroadcastsNothingToOthers()
    {
        var store = await LoadStoreAsync("Skill_Table_SkillLevel.json");
        await using var rig = await Rig.StartAsync(2, store);
        await CancelHandler(rig).HandleAsync(rig.Net.Sessions[0], Request1507(0xDEADBEEFu));

        Assert.Equal(ZoneS2COpcodes.AnsSkillCastingCancel, (await rig.Net.Readers[0].NextAsync()).Op);   // 2710 kept
        Assert.Equal(ZoneS2COpcodes.NfySkillCastingCancel, (await rig.Net.Readers[0].NextAsync()).Op);   // caster's own 2711
        await rig.AssertNothingQueuedAsync(1);                                                              // no map broadcast
    }

    [Fact]
    public async Task CastingCancel_OfAnOpenCast_IsBroadcast_OnceTheBarIsClosed()
    {
        var store = await LoadStoreAsync("Skill_Table_SkillLevel.json");
        await using var rig = await Rig.StartAsync(2, store);
        var handler = CancelHandler(rig);
        SkillCastLandings.BeginCasting(rig.Caster, L5, DateTime.UtcNow);

        await handler.HandleAsync(rig.Net.Sessions[0], Request1507(L5));
        Assert.Equal(ZoneS2COpcodes.AnsSkillCastingCancel, (await rig.Net.Readers[0].NextAsync()).Op);
        Assert.Equal(ZoneS2COpcodes.NfySkillCastingCancel, (await rig.Net.Readers[0].NextAsync()).Op);
        Assert.Equal(ZoneS2COpcodes.NfySkillCastingCancel, (await rig.Net.Readers[1].NextAsync()).Op);

        // the bar is closed now: a second cancel of it is not an active cast any more
        await handler.HandleAsync(rig.Net.Sessions[0], Request1507(L5));
        await rig.Net.Readers[0].NextAsync();
        await rig.Net.Readers[0].NextAsync();
        await rig.AssertNothingQueuedAsync(1);
    }

    [Fact]
    public void ActiveCast_IsAnOpenBar_OrAnUnexpiredPendingCastOfThatExactId()
    {
        var p = new PlayerState();
        var t = new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);
        Assert.False(SkillCastLandings.IsCasting(p, L5, t));
        SkillCastLandings.RecordPending(p, L5, 0, t);                  // an accepted 1508
        Assert.True(SkillCastLandings.IsCasting(p, L5, t.AddSeconds(1)));
        Assert.False(SkillCastLandings.IsCasting(p, L1, t.AddSeconds(1)));
        Assert.False(SkillCastLandings.IsCasting(p, L5, t + SkillCastLandings.PendingGrace + TimeSpan.FromSeconds(1)));
        SkillCastLandings.BeginCasting(p, L10, t);                     // an open 1506 bar
        Assert.True(SkillCastLandings.IsCasting(p, L10, t.AddSeconds(1)));
        SkillCastLandings.EndCasting(p, L10);
        Assert.False(SkillCastLandings.IsCasting(p, L10, t.AddSeconds(1)));
    }

    // ---- CB-H10: 1561 ReqSkillStart_Item ------------------------------------------------------------------------------

    private const uint ConsumableItemSkill = 9201001;
    private const uint ConsumableItemId = 21000001;

    private static byte[] Request1561(uint skillId, uint casterHandle, ulong uid)
    {
        var req = new byte[55];
        BinaryPrimitives.WriteUInt32LittleEndian(req, skillId);
        BinaryPrimitives.WriteUInt32LittleEndian(req.AsSpan(16), casterHandle);
        BinaryPrimitives.WriteUInt64LittleEndian(req.AsSpan(47), uid);
        return req;
    }

    [Theory]
    [InlineData((byte)1, false)]   // worn
    [InlineData((byte)2, false)]   // bank
    [InlineData((byte)3, false)]   // an extra-bag item
    [InlineData((byte)0, true)]    // the bag: the control that must keep working
    public async Task ItemSkill_IsUsedFromTheBagOnly(byte storageType, bool accepted)
    {
        const ulong uid = 0xB0A7u;
        var store = await LoadStoreAsync("ItemInfo.json", "Skill_Table_SkillLevel.json");
        GearInventory? inventory = null;
        await using var rig = await Rig.StartAsync(1, store, configure: c =>
        {
            inventory = new GearInventory(new[]
            {
                new InventoryItem { Id = uid, CharacterId = 4242, ItemId = ConsumableItemId, Count = 5,
                    StorageType = storageType, Slot = 0 },
            });
            c.AddSingleton<RO2.Inventory.Domain.IInventoryRepository>(inventory);
        });
        var handler = new SkillStartItemHandler(rig.Scopes, NullLogger<SkillStartItemHandler>.Instance, rig.Players, rig.Broadcast);
        await handler.HandleAsync(rig.Net.Sessions[0], Request1561(ConsumableItemSkill, rig.Caster.EntityId, uid));

        var count = inventory!.Items.Single(i => i.Id == uid).Count;
        if (accepted)
        {
            Assert.Equal(4, count);
            Assert.Equal(1, SkillCastLandings.PendingCount(rig.Caster, DateTime.UtcNow));
            return;
        }
        // the lone 2711, nothing consumed, no cast recorded
        Assert.Equal(ZoneS2COpcodes.NfySkillCastingCancel, (await rig.Net.Readers[0].NextAsync()).Op);
        await rig.AssertNothingQueuedAsync();
        Assert.Equal(5, count);
        Assert.Equal(0, SkillCastLandings.PendingCount(rig.Caster, DateTime.UtcNow));
    }

    [Fact]
    public async Task ItemSkill_DeadCaster_GetsTheLone2711_NothingConsumed()
    {
        const ulong uid = 0xB0A8u;
        var store = await LoadStoreAsync("ItemInfo.json", "Skill_Table_SkillLevel.json");
        GearInventory? inventory = null;
        await using var rig = await Rig.StartAsync(1, store, tweak: p => p.IsDead = true, configure: c =>
        {
            inventory = new GearInventory(new[]
            {
                new InventoryItem { Id = uid, CharacterId = 4242, ItemId = ConsumableItemId, Count = 5, StorageType = 0, Slot = 0 },
            });
            c.AddSingleton<RO2.Inventory.Domain.IInventoryRepository>(inventory);
        });
        var handler = new SkillStartItemHandler(rig.Scopes, NullLogger<SkillStartItemHandler>.Instance, rig.Players, rig.Broadcast);
        await handler.HandleAsync(rig.Net.Sessions[0], Request1561(ConsumableItemSkill, rig.Caster.EntityId, uid));

        Assert.Equal(ZoneS2COpcodes.NfySkillCastingCancel, (await rig.Net.Readers[0].NextAsync()).Op);
        await rig.AssertNothingQueuedAsync();
        Assert.Equal(5, inventory!.Items.Single(i => i.Id == uid).Count);
        Assert.Equal(0, SkillCastLandings.PendingCount(rig.Caster, DateTime.UtcNow));
    }
}
