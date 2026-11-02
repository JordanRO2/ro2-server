using System.Buffers.Binary;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Quests.Domain;
using RO2.GameData;
using RO2.Inventory.Domain;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;
using static RO2.Tests.Packets.ReauditShared;

namespace RO2.Tests.QuestCharacterization;

/// <summary>
/// B8 S1: the quest rules that live INSIDE the host's async paths (kill / collect / use-item advance, accept, complete,
/// give-up, prize), pinned by driving the real code over a loopback session and reading the frames and counters it leaves.
/// Quest ids are synthetic (990000xxx), so the lazy data/ tables never match them. Reflection is used only for the two
/// private entry points that have no other seam: LootService.AdvanceKillQuestsAsync and ItemUseHandler.AdvanceUseItemQuestsAsync;
/// a carve must keep those names (or move these tests with them).
/// </summary>
public class QuestHandlerFlowCharacterizationTests
{
    private const ushort OpMonster = ZoneS2COpcodes.OnQuestMonsterOrGO;         // 2425
    private const ushort OpComplete = ZoneS2COpcodes.OnQuestCompleteNotify;     // 2427
    private const ushort OpInComplete = ZoneS2COpcodes.OnQuestInCompleteNotify; // 2428
    private const ushort OpAddItem = ZoneS2COpcodes.OnQuestAddItem;             // 2424
    private const ushort OpUpdate = ZoneS2COpcodes.OnQuestUpdateNotify;         // 2432

    // ---- rig ----------------------------------------------------------------------------------------------------------

    private sealed class RecordingQuests : IQuestRepository
    {
        public List<(uint QuestId, string? Json)> Saved { get; } = [];
        public List<uint> Accepted { get; } = [];
        public List<uint> Completed { get; } = [];
        public List<uint> GivenUp { get; } = [];
        public Task<IReadOnlyList<CharacterQuest>> GetActiveQuestsAsync(uint characterId) => Task.FromResult<IReadOnlyList<CharacterQuest>>([]);
        public Task<IReadOnlyList<uint>> GetCompletedQuestIdsAsync(uint characterId) => Task.FromResult<IReadOnlyList<uint>>([]);
        public Task<DateTime?> GetLastCompletedAtAsync(uint characterId, uint questId) => Task.FromResult<DateTime?>(null);
        public Task AcceptQuestAsync(uint characterId, uint questId) { Accepted.Add(questId); return Task.CompletedTask; }
        public Task CompleteQuestAsync(uint characterId, uint questId) { Completed.Add(questId); return Task.CompletedTask; }
        public Task GiveUpQuestAsync(uint characterId, uint questId) { GivenUp.Add(questId); return Task.CompletedTask; }
        public Task SetQuestsTracedAsync(uint characterId, IReadOnlyCollection<uint> tracedQuestIds) => Task.CompletedTask;
        public Task SaveProgressAsync(uint characterId, uint questId, string? progressJson) { Saved.Add((questId, progressJson)); return Task.CompletedTask; }
    }

    private sealed class Rig : IAsyncDisposable
    {
        public required Harness Net { get; init; }
        public required PlayerStateManager Players { get; init; }
        public required BroadcastService Broadcast { get; init; }
        public required GameDataStore Store { get; init; }
        public required ServiceProvider Services { get; init; }
        public required RecordingQuests Quests { get; init; }
        public required GearInventory Bag { get; init; }
        public required PlayerState Player { get; init; }
        public required LootService Loot { get; init; }
        public ProudSession Session => Net.Sessions[0];

        public static async Task<Rig> StartAsync(QuestData[] rows, params InventoryItem[] bag)
        {
            var store = await Q.StoreAsync(rows);
            var net = await Harness.StartAsync(1);
            var players = new PlayerStateManager(new NoDirtySink());
            var broadcast = new BroadcastService(players, NullLogger<BroadcastService>.Instance);
            broadcast.SetServer(net.Server);
            var player = new PlayerState
            {
                SessionId = net.Sessions[0].SessionId, EntityId = 0x07087E11, CharacterId = 1, Name = "Quester",
                MapId = 1100, MapLoaded = true,
            };
            players.AddPlayer(player.SessionId, player);
            var quests = new RecordingQuests();
            var inventory = new GearInventory(bag);
            var services = new ServiceCollection()
                .AddSingleton(store)
                .AddSingleton<IInventoryRepository>(inventory)
                .AddSingleton<IQuestRepository>(quests)
                .AddSingleton(new EffectService(players, broadcast, store, npcState: null!, loot: null!, scopeFactory: null!,
                    logger: NullLogger<EffectService>.Instance, emblems: TestEmblems.Create()))
                .BuildServiceProvider();
            var npcs = new NpcStateManager(store, NullLogger<NpcStateManager>.Instance);
            var combat = new CombatService(players, npcs, broadcast, store, NullLogger<CombatService>.Instance);
            var party = new PartyService(players, broadcast, NullLogger<PartyService>.Instance);
            var loot = new LootService(store, broadcast, combat, npcs, party, players,
                services.GetRequiredService<IServiceScopeFactory>(), NullLogger<LootService>.Instance);
            return new Rig
            {
                Net = net, Players = players, Broadcast = broadcast, Store = store, Services = services, Quests = quests,
                Bag = inventory, Player = player, Loot = loot,
            };
        }

        public IServiceScopeFactory Scopes => Services.GetRequiredService<IServiceScopeFactory>();

        /// <summary>Holds a quest the way a 1374 accept leaves it: in the active set with a zero counter.</summary>
        public void Hold(uint questId)
        {
            Player.ActiveQuests.Add(questId);
            Player.QuestKillProgress[questId] = 0;
        }

        /// <summary>Every frame sent since the last drain (a sentinel 2394 marks the end).</summary>
        public async Task<List<(ushort Op, byte[] Body)>> DrainAsync()
        {
            await Session.SendInternalRmiAsync(ZoneS2COpcodes.OnLevelUp, new byte[] { 0x5A });
            var frames = new List<(ushort, byte[])>();
            while (true)
            {
                var frame = await Net.Readers[0].NextAsync();
                if (frame.Op == ZoneS2COpcodes.OnLevelUp) return frames;
                frames.Add(frame);
            }
        }

        public Task KillAsync(uint npcId)
            => (Task)typeof(LootService).GetMethod("AdvanceKillQuestsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(Loot, [Session, Player, npcId])!;

        public Task<bool> UseItemAsync(uint itemId)
        {
            var handler = new ItemUseHandler(Scopes, NullLogger<ItemUseHandler>.Instance, Players, Broadcast);
            return (Task<bool>)typeof(ItemUseHandler).GetMethod("AdvanceUseItemQuestsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(handler, [Session, Player, itemId, Store, Services])!;
        }

        public ValueTask DisposeAsync()
        {
            Services.Dispose();
            return Net.DisposeAsync();
        }
    }

    private static string Ops(List<(ushort Op, byte[] Body)> frames) => string.Join(",", frames.Select(f => f.Op));

    private static byte[] Request(int skip, params uint[] words)
    {
        var bytes = new byte[skip + 4 * words.Length];
        for (int i = 0; i < words.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(skip + 4 * i), words[i]);
        return bytes;
    }

    private static void AssertFrame(byte[] expected, (ushort Op, byte[] Body) frame, ushort op)
    {
        Assert.Equal(op, frame.Op);
        Assert.Equal(Hex(expected), Hex(frame.Body));
    }

    // ---- 3. kill advance ------------------------------------------------------------------------------------------

    [Fact]
    public async Task KillAdvance_SingleRow_TicksTheRunningCount_ClosesAtTheNeed_ThenStopsAndStoresTheDocument()
    {
        const uint q = 990000011;
        await using var rig = await Rig.StartAsync([Q.Row(2, 7000, 3, questId: q)]);
        rig.Hold(q);

        await rig.KillAsync(7000);
        var f1 = await rig.DrainAsync();
        Assert.Equal("2425", Ops(f1));
        AssertFrame(QuestProgressPackets.BuildQuestMonsterOrGo2425(q, 7000, 1), f1[0], OpMonster);
        Assert.Equal((ushort)1, rig.Player.QuestKillProgress[q]);
        Assert.False(LootService.KillObjectiveClosed(rig.Player, q));
        Assert.Equal((q, "{\"kill\":[1],\"met\":[0]}"), rig.Quests.Saved[^1]);

        await rig.KillAsync(7000);
        AssertFrame(QuestProgressPackets.BuildQuestMonsterOrGo2425(q, 7000, 2), (await rig.DrainAsync()).Single(), OpMonster);

        await rig.KillAsync(7000);
        var f3 = await rig.DrainAsync();
        Assert.Equal("2425,2427", Ops(f3));                                   // the closing 2427 follows the tick that meets the need
        AssertFrame(QuestProgressPackets.BuildQuestMonsterOrGo2425(q, 7000, 3), f3[0], OpMonster);
        AssertFrame(QuestProgressPackets.BuildQuestComplete2427(q), f3[1], OpComplete);
        Assert.True(LootService.KillObjectiveClosed(rig.Player, q));
        Assert.Equal((q, "{\"kill\":[3],\"met\":[0]}"), rig.Quests.Saved[^1]);
        var savedBefore = rig.Quests.Saved.Count;

        await rig.KillAsync(7000);                                            // already met: no tick, no 2427, no save
        Assert.Empty(await rig.DrainAsync());
        Assert.Equal((ushort)3, rig.Player.QuestKillProgress[q]);
        Assert.Equal(savedBefore, rig.Quests.Saved.Count);
        Assert.Contains(q, rig.Player.ActiveQuests);                          // a plain kill quest is not paid here
    }

    [Fact]
    public async Task KillAdvance_IgnoresOtherNpcs_InactiveQuests_AndQuestsMissingFromTheTable()
    {
        const uint q = 990000012;
        await using var rig = await Rig.StartAsync([Q.Row(2, 7000, 2, questId: q), Q.Row(2, 7000, 2, questId: q + 1)]);
        rig.Hold(q);                                                          // q + 1 is in the table but not held

        await rig.KillAsync(7777);
        rig.Player.ActiveQuests.Add(5);                                       // held, but not in Quest_Info
        await rig.KillAsync(7000);
        var frames = await rig.DrainAsync();
        Assert.Equal("2425", Ops(frames));
        AssertFrame(QuestProgressPackets.BuildQuestMonsterOrGo2425(q, 7000, 1), frames[0], OpMonster);
        Assert.False(rig.Player.QuestKillProgress.ContainsKey(q + 1));
    }

    [Fact]
    public async Task KillAdvance_CountIsU16_NeedCappedAt65535_NoByteCap()
    {
        const uint a = 990000013, b = 990000014;
        await using var rig = await Rig.StartAsync([Q.Row(2, 7000, 300, questId: a), Q.Row(2, 7100, 70000, questId: b)]);
        rig.Hold(a);
        rig.Hold(b);
        rig.Player.QuestKillProgress[a] = 299;
        rig.Player.QuestKillProgress[b] = 65534;

        await rig.KillAsync(7000);
        var fa = await rig.DrainAsync();
        Assert.Equal("2425,2427", Ops(fa));
        AssertFrame(QuestProgressPackets.BuildQuestMonsterOrGo2425(a, 7000, 300), fa[0], OpMonster);     // 300 > 255 ticks correctly

        await rig.KillAsync(7100);
        var fb = await rig.DrainAsync();
        Assert.Equal("2425,2427", Ops(fb));
        AssertFrame(QuestProgressPackets.BuildQuestMonsterOrGo2425(b, 7100, 65535), fb[0], OpMonster);   // need 70000 is met at 65535

        await rig.KillAsync(7100);
        Assert.Empty(await rig.DrainAsync());
        Assert.Equal((ushort)65535, rig.Player.QuestKillProgress[b]);
    }

    // QUIRK: two LATER kill rows on the same NPC share ONE counter (keyed by NPC id), so a single kill ticks both rows
    // (1 then 2), and the pair is met after two kills although each row needs three.
    [Fact]
    public async Task KillAdvance_LaterRowsOnTheSameNpc_ShareOneCounter_OneKillTicksTwice()
    {
        const uint q = 990000015;
        await using var rig = await Rig.StartAsync([Q.Row(2, 7000, 2, questId: q), Q.Row(2, 7001, 3, questId: q), Q.Row(2, 7001, 3, questId: q)]);
        rig.Hold(q);

        await rig.KillAsync(7000);
        AssertFrame(QuestProgressPackets.BuildQuestMonsterOrGo2425(q, 7000, 1), (await rig.DrainAsync()).Single(), OpMonster);

        await rig.KillAsync(7001);
        var two = await rig.DrainAsync();
        Assert.Equal("2425,2425", Ops(two));
        AssertFrame(QuestProgressPackets.BuildQuestMonsterOrGo2425(q, 7001, 1), two[0], OpMonster);       // row 1
        AssertFrame(QuestProgressPackets.BuildQuestMonsterOrGo2425(q, 7001, 2), two[1], OpMonster);       // row 2 continues the SAME counter
        Assert.Equal((q, "{\"kill\":[1,2,2],\"met\":[0,0,0]}"), rig.Quests.Saved[^1]);

        await rig.KillAsync(7001);                                            // row 1 reaches 3; row 2 is already >= its need
        var third = await rig.DrainAsync();
        AssertFrame(QuestProgressPackets.BuildQuestMonsterOrGo2425(q, 7001, 3), third.Single(), OpMonster);
        Assert.False(LootService.AllKillObjectivesMet(rig.Player, q, rig.Store.QuestRows[q]));

        await rig.KillAsync(7000);                                            // the first row closes the quest
        var last = await rig.DrainAsync();
        Assert.Equal("2425,2427", Ops(last));
        AssertFrame(QuestProgressPackets.BuildQuestMonsterOrGo2425(q, 7000, 2), last[0], OpMonster);
        Assert.True(LootService.KillObjectiveClosed(rig.Player, q));
        Assert.Equal((q, "{\"kill\":[2,3,3],\"met\":[0,0,0]}"), rig.Quests.Saved[^1]);
    }

    // QUIRK: when the first-row counter drops below the value seen at the previous kill, the later-row counts are wiped.
    [Fact]
    public async Task KillAdvance_LoweredFirstRowCounter_WipesTheLaterRowCounts()
    {
        const uint q = 990000016;
        await using var rig = await Rig.StartAsync([Q.Row(2, 7000, 3, questId: q), Q.Row(2, 7001, 3, questId: q)]);
        rig.Hold(q);

        await rig.KillAsync(7000);
        await rig.KillAsync(7000);
        await rig.KillAsync(7001);
        await rig.DrainAsync();
        Assert.Equal((ushort)1, LootService.ExtraKillCount(rig.Player, q, 7001));

        rig.Player.QuestKillProgress[q] = 0;                                  // lowered, as a collect-style drop would
        await rig.KillAsync(7001);
        AssertFrame(QuestProgressPackets.BuildQuestMonsterOrGo2425(q, 7001, 1), (await rig.DrainAsync()).Single(), OpMonster);   // 1, not 2
    }

    // ---- collect advance: the byte-255 cap on the FIRST Quest_Info row ----------------------------------------------------

    [Fact]
    public async Task CollectAdvance_NeedIsByteCapped_FirstRowOnly_CounterJumpsToTheCap()
    {
        const uint q = 990000021;
        await using var rig = await Rig.StartAsync([Q.Row(4, 5000, 256, questId: q)]);
        rig.Hold(q);

        await rig.Loot.AdvanceCollectQuestsAsync(rig.Session, rig.Player, 5000, 300);
        var frames = await rig.DrainAsync();
        Assert.Equal("2424,2427", Ops(frames));                               // need 256 is met at 255: byte cap, unlike the store's u16 need
        AssertFrame(RO2.Server.Zone.Services.QuestNotifyPackets.BuildQuestAddItem2424(q, 5000, 255), frames[0], OpAddItem);
        AssertFrame(QuestProgressPackets.BuildQuestComplete2427(q), frames[1], OpComplete);
        Assert.Equal((ushort)255, rig.Player.QuestKillProgress[q]);

        await rig.Loot.AdvanceCollectQuestsAsync(rig.Session, rig.Player, 5000, 1);
        Assert.Empty(await rig.DrainAsync());                                 // already >= the byte need

        // ...but the store side still reads 255 held as NOT met for need 256, and the turn-in closed flag stays false
        Assert.Equal("[255]/False", Show(QuestProgressStore.LoginProgress(rig.Store.QuestRows[q], null, [Q.Stack(5000, 255)])));
    }

    [Fact]
    public async Task CollectAdvance_AddsMaxOfCountAndOne_StopsAtNeed_SkipsOtherItemsTypesAndLaterRows()
    {
        const uint q = 990000022, d = 990000023, l = 990000024, z = 990000025;
        await using var rig = await Rig.StartAsync([
            Q.Row(4, 5000, 3, questId: q),
            Q.Row(5, 5000, 3, questId: d),                                    // deliver: not advanced by a pick-up
            Q.Row(2, 7000, 1, questId: l), Q.Row(4, 5000, 3, questId: l),     // collect on a LATER row: not advanced
            Q.Row(4, 5000, 0, questId: z)]);                                  // need 0 reads as 1
        foreach (var id in new[] { q, d, l, z }) rig.Hold(id);

        await rig.Loot.AdvanceCollectQuestsAsync(rig.Session, rig.Player, 5000, 0);       // count 0 reads as 1
        var first = await rig.DrainAsync();
        Assert.Equal("2424,2424,2427", Ops(first));
        AssertFrame(RO2.Server.Zone.Services.QuestNotifyPackets.BuildQuestAddItem2424(q, 5000, 1), first[0], OpAddItem);
        AssertFrame(RO2.Server.Zone.Services.QuestNotifyPackets.BuildQuestAddItem2424(z, 5000, 1), first[1], OpAddItem);
        AssertFrame(QuestProgressPackets.BuildQuestComplete2427(z), first[2], OpComplete);
        Assert.Equal((ushort)1, rig.Player.QuestKillProgress[q]);
        Assert.Equal((ushort)0, rig.Player.QuestKillProgress[d]);
        Assert.Equal((ushort)0, rig.Player.QuestKillProgress[l]);

        await rig.Loot.AdvanceCollectQuestsAsync(rig.Session, rig.Player, 5000, 5);       // min(1 + 5, 3)
        var second = await rig.DrainAsync();
        Assert.Equal("2424,2427", Ops(second));
        AssertFrame(RO2.Server.Zone.Services.QuestNotifyPackets.BuildQuestAddItem2424(q, 5000, 3), second[0], OpAddItem);
        Assert.Equal((ushort)3, rig.Player.QuestKillProgress[q]);

        await rig.Loot.AdvanceCollectQuestsAsync(rig.Session, rig.Player, 9999, 1);       // another item
        Assert.Empty(await rig.DrainAsync());
    }

    private static string Show((ushort[] Values, bool AllMet) r) => $"[{string.Join(",", r.Values)}]/{r.AllMet}";

    // ---- 4. use-item advance -----------------------------------------------------------------------------------------

    [Fact]
    public async Task UseItemAdvance_TicksEachUse_ClosesWhenEveryUseItemRowIsMet_NullOnceMet()
    {
        const uint q = 990000031;
        await using var rig = await Rig.StartAsync([Q.Row(6, 0, 5000, 2, questId: q)]);
        rig.Hold(q);

        Assert.True(await rig.UseItemAsync(5000));
        var one = await rig.DrainAsync();
        Assert.Equal("2428", Ops(one));
        AssertFrame(QuestItemObjectivePackets.BuildQuestInCompleteNotify2428(q, 5000, 1), one[0], OpInComplete);
        Assert.Equal((q, "{\"kill\":[0],\"met\":[1]}"), rig.Quests.Saved[^1]);            // partial: the use count is stored under "met"
        Assert.Equal((ushort)0, rig.Player.QuestKillProgress[q]);

        Assert.True(await rig.UseItemAsync(5000));
        var two = await rig.DrainAsync();
        Assert.Equal("2428,2427", Ops(two));
        AssertFrame(QuestItemObjectivePackets.BuildQuestInCompleteNotify2428(q, 5000, 2), two[0], OpInComplete);
        Assert.Equal((ushort)1, rig.Player.QuestKillProgress[q]);                          // the bare closed flag
        Assert.Equal((q, "{\"kill\":[0],\"met\":[2]}"), rig.Quests.Saved[^1]);
        Assert.Contains(q, rig.Player.ActiveQuests);

        Assert.False(await rig.UseItemAsync(5000));                                        // already met: no tick, nothing ticked
        Assert.Empty(await rig.DrainAsync());
        Assert.False(await rig.UseItemAsync(1234));                                        // another item
    }

    [Fact]
    public async Task UseItemAdvance_NeedIsByteCappedAt255_AndSeveralRowsAllHaveToBeMet()
    {
        const uint big = 990000032, multi = 990000033;
        await using var rig = await Rig.StartAsync([
            Q.Row(6, 0, 5000, 300, questId: big),
            Q.Row(6, 0, 6000, 1, questId: multi), Q.Row(6, 0, 6001, 0, questId: multi), Q.Row(0, questId: multi)]);
        rig.Hold(big);
        rig.Hold(multi);

        QuestUseItemProgress.Restore(rig.Player, big, 1, 0, 254);
        Assert.True(await rig.UseItemAsync(5000));
        var f = await rig.DrainAsync();
        Assert.Equal("2428,2427", Ops(f));                                                  // need 300 closes at 255 uses
        AssertFrame(QuestItemObjectivePackets.BuildQuestInCompleteNotify2428(big, 5000, 255), f[0], OpInComplete);

        Assert.True(await rig.UseItemAsync(6000));                                          // row 0 met, row 1 not: no 2427
        Assert.Equal("2428", Ops(await rig.DrainAsync()));
        Assert.False(rig.Player.QuestKillProgress[multi] == 1);
        Assert.True(await rig.UseItemAsync(6001));                                          // need 0 reads as 1; now both met
        Assert.Equal("2428,2427", Ops(await rig.DrainAsync()));
        Assert.Equal((ushort)1, rig.Player.QuestKillProgress[multi]);
    }

    // ---- 10 + overloaded counter: accept ----------------------------------------------------------------------------------

    [Fact]
    public async Task Accept_UnknownAndAlreadyHeldQuests_AreRefusedWith21703()
    {
        const uint q = 990000041;
        await using var rig = await Rig.StartAsync([Q.Row(1, 10, questId: q)]);
        var handler = new QuestAcceptHandler(rig.Scopes, NullLogger<QuestAcceptHandler>.Instance, rig.Players, rig.Broadcast);

        await handler.HandleAsync(rig.Session, Request(10, 123456));
        AssertFrame(QuestProgressPackets.BuildQuestError2431(123456, QuestProgressPackets.QuestErrorInsertRejected), (await rig.DrainAsync()).Single(), ZoneS2COpcodes.OnQuestError);

        rig.Player.ActiveQuests.Add(q);
        await handler.HandleAsync(rig.Session, Request(10, q));
        AssertFrame(QuestProgressPackets.BuildQuestError2431(q, QuestProgressPackets.QuestErrorInsertRejected), (await rig.DrainAsync()).Single(), ZoneS2COpcodes.OnQuestError);
        Assert.Empty(rig.Quests.Accepted);
        Assert.Equal(21703u, QuestProgressPackets.QuestErrorInsertRejected);
    }

    [Fact]
    public async Task Accept_StartItemQuest_RefusedWith20036WhenTheItemIsNotInTheBag_AcceptedWhenItIs()
    {
        const uint q = 990000042;
        var row = Q.Row(1, 10, questId: q) with { Start_Type = 2, Start_Type_ID = 5555 };
        Assert.Equal(5555u, QuestAcceptGates.StartItemOf(row));
        Assert.Equal(20036u, QuestProgressPackets.QuestInsertStartItemMissing);

        await using (var without = await Rig.StartAsync([row]))
        {
            var handler = new QuestAcceptHandler(without.Scopes, NullLogger<QuestAcceptHandler>.Instance, without.Players, without.Broadcast);
            await handler.HandleAsync(without.Session, Request(10, q));
            AssertFrame(QuestProgressPackets.BuildQuestInsertResult2421(q, 20036), (await without.DrainAsync()).Single(), ZoneS2COpcodes.OnQuestInsert);
            Assert.DoesNotContain(q, without.Player.ActiveQuests);
            Assert.Empty(without.Quests.Accepted);
        }

        await using var holder = await Rig.StartAsync([row], Q.Stack(5555, 1));
        var accept = new QuestAcceptHandler(holder.Scopes, NullLogger<QuestAcceptHandler>.Instance, holder.Players, holder.Broadcast);
        await accept.HandleAsync(holder.Session, Request(10, q));
        AssertFrame(QuestProgressPackets.BuildQuestInsert2421(q), (await holder.DrainAsync()).Single(), ZoneS2COpcodes.OnQuestInsert);
        Assert.Contains(q, holder.Player.ActiveQuests);
        Assert.Equal((ushort)0, holder.Player.QuestKillProgress[q]);
        Assert.Equal(new[] { q }, holder.Quests.Accepted);
    }

    [Theory]
    [InlineData(300u, 400, 300, 255, true)]     // held 400 of need 300: tick 300 (u16), counter byte-capped at 255, closed
    [InlineData(300u, 100, 100, 100, false)]    // partial hold of a single-row quest: the counter is the held count
    [InlineData(256u, 300, 256, 255, true)]     // QUIRK: counter 255 < NeededKills 256 although the 2427 was sent
    [InlineData(255u, 255, 255, 255, true)]
    [InlineData(5u, 0, -1, 0, false)]           // nothing held: no tick, the counter stays at the accept's 0
    public async Task Accept_CollectQuestAlreadyHeld_TicksTheHeldCount_CounterIsByteCapped(uint need, int held, int tickCount, int counter, bool closes)
    {
        const uint q = 990000043;
        await using var rig = await Rig.StartAsync([Q.Row(4, 5000, need, questId: q)], held > 0 ? new[] { Q.Stack(5000, held) } : Array.Empty<InventoryItem>());
        var handler = new QuestAcceptHandler(rig.Scopes, NullLogger<QuestAcceptHandler>.Instance, rig.Players, rig.Broadcast);

        await handler.HandleAsync(rig.Session, Request(10, q));
        var frames = await rig.DrainAsync();

        Assert.Equal(tickCount < 0 ? "2421" : closes ? "2421,2428,2427" : "2421,2428", Ops(frames));
        if (tickCount >= 0)
            AssertFrame(QuestItemObjectivePackets.BuildQuestInCompleteNotify2428(q, 5000, (ushort)tickCount), frames[1], OpInComplete);
        Assert.Equal((ushort)counter, rig.Player.QuestKillProgress[q]);
    }

    [Fact]
    public async Task Accept_BeginRun_ClearsEveryPerRunCounter()
    {
        const uint q = 990000044;
        var player = new PlayerState();
        player.QuestKillProgress[q] = 77;
        LootService.RestoreKillProgress(player, q, [Q.Row(2, 7000, 3), Q.Row(2, 7001, 3)], new ushort[] { 2, 2 });
        QuestUseItemProgress.Restore(player, q, 2, 0, 4);
        QuestTalkProgress.Restore(player, q, [Q.Row(1, 10), Q.Row(1, 20)], [0]);
        QuestFailState.MarkFailed(player, q);

        QuestAcceptHandler.BeginRun(player, q);

        Assert.Contains(q, player.ActiveQuests);
        Assert.Equal((ushort)0, player.QuestKillProgress[q]);
        Assert.Equal((ushort)0, LootService.ExtraKillCount(player, q, 7001));
        Assert.False(LootService.KillObjectiveClosed(player, q));
        Assert.Equal(0, QuestUseItemProgress.Count(player, q, 0));
        Assert.Empty(QuestTalkProgress.MetRows(player, q));
        Assert.True(QuestTalkProgress.AcceptedThisLogin(player, q));
        Assert.False(QuestFailState.IsFailed(player, q));
    }

    // ---- complete (1377): the counter semantics for talk / deliver / everything else ------------------------------------------

    [Fact]
    public async Task Complete_UnknownAndNotHeld_AreRefused21703And21711()
    {
        const uint q = 990000051;
        await using var rig = await Rig.StartAsync([Q.Row(1, 10, questId: q)]);
        var handler = new QuestCompleteHandler(rig.Scopes, NullLogger<QuestCompleteHandler>.Instance, rig.Players, rig.Broadcast);

        await handler.HandleAsync(rig.Session, Request(10, 123456));
        AssertFrame(QuestProgressPackets.BuildQuestError2431(123456, 21703), (await rig.DrainAsync()).Single(), ZoneS2COpcodes.OnQuestError);
        await handler.HandleAsync(rig.Session, Request(10, q));
        AssertFrame(QuestProgressPackets.BuildQuestError2431(q, 21711), (await rig.DrainAsync()).Single(), ZoneS2COpcodes.OnQuestError);
    }

    [Fact]
    public async Task Complete_Talk_FirstReportSendsUpdateThen2427_SetsTheClosedFlag_StoresTheCloseOnce()
    {
        const uint q = 990000052;
        await using var rig = await Rig.StartAsync([Q.Row(1, 10, questId: q, rewardNpc: 11)]);
        rig.Hold(q);
        var handler = new QuestCompleteHandler(rig.Scopes, NullLogger<QuestCompleteHandler>.Instance, rig.Players, rig.Broadcast);

        await handler.HandleAsync(rig.Session, Request(10, q));                           // no giver word: the update targets Value1
        var first = await rig.DrainAsync();
        Assert.Equal("2432,2427", Ops(first));
        AssertFrame(QuestNotifyPackets.BuildQuestUpdateNotify2432(q, 10), first[0], OpUpdate);
        AssertFrame(QuestProgressPackets.BuildQuestComplete2427(q), first[1], OpComplete);
        Assert.Equal((ushort)1, rig.Player.QuestKillProgress[q]);
        Assert.Equal((q, "{\"kill\":[0],\"met\":[1]}"), rig.Quests.Saved.Single());
        Assert.Contains(q, rig.Player.ActiveQuests);                                      // paid at the turn-in, not here

        await handler.HandleAsync(rig.Session, Request(10, q, 4242));                      // counter already 1: only the 2427 again
        var second = await rig.DrainAsync();
        Assert.Equal("2427", Ops(second));
        Assert.Equal(2, rig.Quests.Saved.Count);

        // a fresh run with a giver word aims the update at the giver
        rig.Hold(q);
        await handler.HandleAsync(rig.Session, Request(10, q, 4242));
        AssertFrame(QuestNotifyPackets.BuildQuestUpdateNotify2432(q, 4242), (await rig.DrainAsync())[0], OpUpdate);
    }

    // The deliver counter: max(progress, byte-capped total). QUIRK: a progress above 255 (a u16 counter) is kept and reads as "not closing".
    [Theory]
    [InlineData(300u, 7, 255, "2427")]
    [InlineData(0u, 0, 1, "2427")]
    [InlineData(3u, 3, 3, "2427")]            // progress == total: closes again (the 2427 is sent unconditionally)
    [InlineData(300u, 400, 400, "2427")]
    [InlineData(255u, 255, 255, "2427")]
    public async Task Complete_Deliver_CounterIsMaxOfProgressAndByteCappedTotal(uint need, int progress, int counter, string ops)
    {
        const uint q = 990000053;
        await using var rig = await Rig.StartAsync([Q.Row(5, 5000, need, questId: q)]);
        rig.Hold(q);
        rig.Player.QuestKillProgress[q] = (ushort)progress;
        var handler = new QuestCompleteHandler(rig.Scopes, NullLogger<QuestCompleteHandler>.Instance, rig.Players, rig.Broadcast);

        await handler.HandleAsync(rig.Session, Request(10, q));

        Assert.Equal(ops, Ops(await rig.DrainAsync()));
        Assert.Equal((ushort)counter, rig.Player.QuestKillProgress[q]);
        Assert.Empty(rig.Quests.Saved);                                                   // only talk quests store their close here
    }

    // QUIRK: any other held quest (collect, kill, use-item...) gets a 2427 for the asking and its counter is untouched.
    [Theory]
    [InlineData((byte)4)]
    [InlineData((byte)2)]
    [InlineData((byte)6)]
    public async Task Complete_OtherObjectiveTypes_Send2427AndLeaveTheCounterAlone(byte type)
    {
        const uint q = 990000054;
        await using var rig = await Rig.StartAsync([Q.Row(type, 7000, 5, questId: q)]);
        rig.Hold(q);
        rig.Player.QuestKillProgress[q] = 2;
        var handler = new QuestCompleteHandler(rig.Scopes, NullLogger<QuestCompleteHandler>.Instance, rig.Players, rig.Broadcast);

        await handler.HandleAsync(rig.Session, Request(10, q));

        Assert.Equal("2427", Ops(await rig.DrainAsync()));
        Assert.Equal((ushort)2, rig.Player.QuestKillProgress[q]);
    }

    // ---- give-up ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task GiveUp_HeldQuest_ForgetsEveryCounter_StoresTheGiveUp_Sends2420()
    {
        const uint q = 990000061;
        await using var rig = await Rig.StartAsync([Q.Row(2, 7000, 3, questId: q), Q.Row(2, 7001, 3, questId: q)]);
        rig.Hold(q);
        LootService.RestoreKillProgress(rig.Player, q, rig.Store.QuestRows[q], new ushort[] { 1, 2 });
        QuestUseItemProgress.Restore(rig.Player, q, 2, 0, 3);
        var handler = new QuestGiveUpHandler(rig.Scopes, NullLogger<QuestGiveUpHandler>.Instance, rig.Players, rig.Broadcast);

        await handler.HandleAsync(rig.Session, Request(10, q));

        AssertFrame(QuestProgressPackets.BuildQuestGiveup2420(q), (await rig.DrainAsync()).Single(), ZoneS2COpcodes.OnQuestGiveup);
        Assert.DoesNotContain(q, rig.Player.ActiveQuests);
        Assert.False(rig.Player.QuestKillProgress.ContainsKey(q));
        Assert.Equal((ushort)0, LootService.ExtraKillCount(rig.Player, q, 7001));
        Assert.Equal(0, QuestUseItemProgress.Count(rig.Player, q, 0));
        Assert.Equal(new[] { q }, rig.Quests.GivenUp);
    }

    // QUIRK: the counters are wiped BEFORE the "is it held" test, so a refused give-up still erases a stray counter.
    [Fact]
    public async Task GiveUp_NotHeld_IsRefusedWith21711_ButStillWipesAStrayCounter()
    {
        const uint q = 990000062;
        await using var rig = await Rig.StartAsync([Q.Row(2, 7000, 3, questId: q)]);
        rig.Player.QuestKillProgress[q] = 2;
        var handler = new QuestGiveUpHandler(rig.Scopes, NullLogger<QuestGiveUpHandler>.Instance, rig.Players, rig.Broadcast);

        await handler.HandleAsync(rig.Session, Request(10, q));

        var frame = (await rig.DrainAsync()).Single();
        Assert.Equal(ZoneS2COpcodes.OnQuestGiveup, frame.Op);
        Assert.Equal(q, BinaryPrimitives.ReadUInt32LittleEndian(frame.Body.AsSpan(10)));
        Assert.Equal(21711u, BinaryPrimitives.ReadUInt32LittleEndian(frame.Body.AsSpan(14)));
        Assert.NotEqual(Hex(QuestProgressPackets.BuildQuestGiveup2420(q)), Hex(frame.Body));
        Assert.False(rig.Player.QuestKillProgress.ContainsKey(q));
        Assert.Empty(rig.Quests.GivenUp);
    }

    // ---- execute (1376) -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Execute_IsLoggedOnly_NoReply_NoStateChange()
    {
        const uint q = 990000071;
        await using var rig = await Rig.StartAsync([Q.Row(1, 10, questId: q)]);
        rig.Hold(q);
        var handler = new QuestExecuteHandler(rig.Scopes, NullLogger<QuestExecuteHandler>.Instance, rig.Players, rig.Broadcast);

        await handler.HandleAsync(rig.Session, new byte[24]);
        await handler.HandleAsync(rig.Session, new byte[23]);

        Assert.Empty(await rig.DrainAsync());
        Assert.Contains(q, rig.Player.ActiveQuests);
        Assert.Empty(rig.Quests.Saved);
    }

    // ---- prize (1378): money, the closed flag, the paid-twice refusal -----------------------------------------------------------------

    [Theory]
    [InlineData(12000, 12000u)]
    [InlineData(0, 0u)]
    [InlineData(-5, 0u)]                       // a negative Reward_Money pays 0
    public async Task Prize_PaysMaxOfZeroAndRewardMoney_In2429AndTheWallet(int rewardMoney, uint paid)
    {
        const uint q = 990000081;
        await using var rig = await Rig.StartAsync([Q.Row(1, 10, questId: q, rewardNpc: 10) with { Reward_Money = rewardMoney }]);
        rig.Hold(q);
        var handler = new QuestPrizeHandler(rig.Scopes, NullLogger<QuestPrizeHandler>.Instance, rig.Players, rig.Broadcast);

        await handler.HandleAsync(rig.Session, Request(10, 10, q));                        // giver 10 = the talk / reward NPC

        var frames = await rig.DrainAsync();
        Assert.Equal("2432,2427,2429,2435", Ops(frames));
        AssertFrame(QuestProgressPackets.BuildQuestPrize2429(q, 0, paid), frames[2], ZoneS2COpcodes.OnQuestPrize);
        AssertFrame(ActorVitalsPackets.BuildZenyChange2435(rig.Player.EntityId, paid), frames[3], ZoneS2COpcodes.OnZenyChange);
        Assert.Equal((long)paid, rig.Player.Gold);
        Assert.DoesNotContain(q, rig.Player.ActiveQuests);
        Assert.False(rig.Player.QuestKillProgress.ContainsKey(q));
        Assert.Equal(new[] { q }, rig.Quests.Completed);

        // paid once: the same turn-in again is refused with 21711, not paid
        await handler.HandleAsync(rig.Session, Request(10, 10, q));
        AssertFrame(QuestProgressPackets.BuildQuestError2431(q, 21711), (await rig.DrainAsync()).Single(), ZoneS2COpcodes.OnQuestError);
        Assert.Equal((long)paid, rig.Player.Gold);
    }

    // The turn-in sends the closing 2427 only when the quest is not "closed this session" by its counter.
    [Theory]
    [InlineData(3u, 0, true)]                  // collect need 3, nothing counted: 2427 at the turn-in
    [InlineData(3u, 3, false)]                 // counter reached the need: already closed
    [InlineData(256u, 255, true)]              // QUIRK: need 256 caps the live counter at 255, so the 2427 is sent AGAIN
    [InlineData(256u, 256, false)]
    public async Task Prize_Collect_Sends2427OnlyWhenTheCounterHasNotReachedTheU16Need(uint need, int counter, bool sends2427)
    {
        const uint q = 990000082;
        await using var rig = await Rig.StartAsync([Q.Row(4, 5000, need, questId: q, rewardNpc: 10)], Q.Stack(5000, (int)need));
        rig.Hold(q);
        rig.Player.QuestKillProgress[q] = (ushort)counter;
        var handler = new QuestPrizeHandler(rig.Scopes, NullLogger<QuestPrizeHandler>.Instance, rig.Players, rig.Broadcast);

        await handler.HandleAsync(rig.Session, Request(10, 10, q));

        var frames = await rig.DrainAsync();
        Assert.Equal(sends2427, frames.Any(f => f.Op == OpComplete));
        Assert.Contains(frames, f => f.Op == ZoneS2COpcodes.OnQuestPrize);
        Assert.Empty(rig.Bag.Items);                                                       // the objective items were handed over: ObjectiveItemCount of them
    }

    [Fact]
    public async Task Prize_CollectNotMet_IsIgnoredInSilence_QuestStaysHeld()
    {
        const uint q = 990000083;
        await using var rig = await Rig.StartAsync([Q.Row(4, 5000, 3, questId: q, rewardNpc: 10)], Q.Stack(5000, 2));
        rig.Hold(q);
        var handler = new QuestPrizeHandler(rig.Scopes, NullLogger<QuestPrizeHandler>.Instance, rig.Players, rig.Broadcast);

        await handler.HandleAsync(rig.Session, Request(10, 10, q));

        Assert.Empty(await rig.DrainAsync());
        Assert.Contains(q, rig.Player.ActiveQuests);
        Assert.Empty(rig.Quests.Completed);
    }

    // ---- 10. the daily gate at the reset boundary ------------------------------------------------------------------------------

    [Fact]
    public void DailyGate_ResetBoundary_IsInclusiveAtExactly1300Utc()
    {
        static DateTime U(int day, int h, int m, int s, int ticks = 0)
            => new DateTime(2026, 7, day, h, m, s, DateTimeKind.Utc).AddTicks(ticks);

        Assert.Equal(TimeSpan.FromHours(13), QuestRepeatGate.DailyResetUtc);
        // now = the reset instant: a completion AT the reset blocks, one tick before it does not
        Assert.True(QuestRepeatGate.DoneToday(U(20, 13, 0, 0), U(20, 13, 0, 0)));
        Assert.False(QuestRepeatGate.DoneToday(U(20, 13, 0, 0).AddTicks(-1), U(20, 13, 0, 0)));
        // one tick before the reset the previous day's reset is the latest
        Assert.Equal(U(19, 13, 0, 0), QuestRepeatGate.LatestDailyReset(U(20, 13, 0, 0).AddTicks(-1)));
        Assert.True(QuestRepeatGate.DoneToday(U(19, 13, 0, 0), U(20, 12, 59, 59, 9_999_999)));
        Assert.False(QuestRepeatGate.DoneToday(U(19, 13, 0, 0).AddTicks(-1), U(20, 12, 59, 59, 9_999_999)));
        // a completion in the future of "now" still counts as done today
        Assert.True(QuestRepeatGate.DoneToday(U(22, 5, 0, 0), U(20, 14, 0, 0)));
        Assert.False(QuestRepeatGate.DoneToday(null, U(20, 14, 0, 0)));
        // an Unspecified kind is read as UTC for the completion and used as is for "now"
        Assert.True(QuestRepeatGate.DoneToday(DateTime.SpecifyKind(U(20, 13, 0, 0), DateTimeKind.Unspecified), DateTime.SpecifyKind(U(20, 14, 0, 0), DateTimeKind.Unspecified)));
        Assert.Equal(DateTimeKind.Utc, QuestRepeatGate.LatestDailyReset(U(20, 14, 0, 0)).Kind);
    }
}
