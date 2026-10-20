using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
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
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Tests for the Khara engine: the catalog's rows and PC_Job gate (2574), the Complete_Type 14, 15 and 30 conditions (2575 and 2577), the profession EXP rate on a craft, the class, profession, Job_Master and start-item gates of 1374, and the talk refusal and side-close of 1378.
/// </summary>
public class Reaudit2_A1_Tests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);

    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    
    
    
 /// <summary>
 /// The catalog reads the NationEnable-4 block of the ids with two: 413 is the auto-accept for job 16 (PC_Job 32768), 403 has its three condition rows, 418 is the skill 6204 auto, and the single-block 360 and 180 keep their rows.
 /// </summary>
    [Fact]
    public async Task KharaCatalog_ReadsTheRetailNationBlock()
    {
        var catalog = KharaCatalog.For(await LoadStoreAsync("Khara_Info.json"));
        var k413 = catalog.Get(413)!;
        Assert.Equal(4, k413.Main.NationEnable);
        Assert.True(k413.AutoChallenge);
        var row = Assert.Single(k413.Conditions);
        Assert.Equal((KharaService.CompleteUseSkill, 6106u, 30u), (row.Complete_Type, row.Complete_Type_Value1, row.Complete_Type_Value2));
        Assert.Equal(3, catalog.Get(403)!.Conditions.Count);
        Assert.Equal(6204u, catalog.Get(418)!.Conditions.Single().Complete_Type_Value1);
        Assert.Equal(11, catalog.Get(360)!.Conditions.Count);
        Assert.Equal(3, catalog.Get(180)!.Conditions.Count);
        Assert.Equal(9, catalog.Get(423)!.Conditions.Count);
    }

 /// <summary>
 /// A cast of skill group 6207 by a job-17 character auto-accepts 417 (PC_Job 65536), sending 2574 and then a 2577 for its row 0. A job-16 character gets nothing for that cast.
 /// </summary>
    [Fact]
    public async Task KharaSkillAuto_417_IsTheLiveFrames_AndHonoursPcJob()
    {
        var catalog = KharaCatalog.For(await LoadStoreAsync("Khara_Info.json"));
        var job17 = new KharaEngine(catalog, new KharaStateDocument(), new KharaFacts { Level = 50, CombatClass = 17 });
        job17.OnCountedEvent(KharaService.CompleteUseSkill, 6207);
        Assert.Equal(new[] { "2574:a10100000000", "2577:a1010000003f1800000100" }, KharaFrames(job17));

        var job16 = new KharaEngine(catalog, new KharaStateDocument(), new KharaFacts { Level = 50, CombatClass = 16 });
        job16.OnCountedEvent(KharaService.CompleteUseSkill, 6207);
        Assert.Empty(job16.Frames);
    }

 /// <summary>
 /// 413 (skill group 6106, PC_Job 32768) sends its first count on the cast. A job-17 character is never accepted, and the unknown class 0 or the Novice class has no PC_Job bit.
 /// </summary>
    [Fact]
    public async Task KharaSkillAuto_413_ForJob16Only()
    {
        var catalog = KharaCatalog.For(await LoadStoreAsync("Khara_Info.json"));
        var job16 = new KharaEngine(catalog, new KharaStateDocument(), new KharaFacts { Level = 10, CombatClass = 16 });
        job16.OnCountedEvent(KharaService.CompleteUseSkill, 6106);
        Assert.Equal(new[] { "2574:9d0100000000", "2577:9d01000000da1700000100" }, KharaFrames(job16));

        foreach (byte cls in new byte[] { 17, 18, 0, 100 })
        {
            var other = new KharaEngine(catalog, new KharaStateDocument(), new KharaFacts { Level = 10, CombatClass = cls });
            other.OnCountedEvent(KharaService.CompleteUseSkill, 6106);
            Assert.Empty(other.Frames);
        }
        Assert.True(catalog.Get(180)!.JobAllows(0));   
    }

    
    
    
 /// <summary>
 /// The "Combat level 50 achieved" condition completes when the level reaches 50: it sends 2575 and then 2576 with a zero reward. Below level 50 nothing is sent, and the row never sends a 2577.
 /// </summary>
    [Fact]
    public async Task KharaBaseLevel_54_CompletesOnTheLevelUp()
    {
        var catalog = KharaCatalog.For(await LoadStoreAsync("Khara_Info.json"));
        var below = new KharaEngine(catalog, Holding(54, 0), new KharaFacts { Level = 49 });
        below.OnLevelChanged();
        Assert.Empty(below.Frames);

        var reached = new KharaEngine(catalog, Holding(54, 0), new KharaFacts { Level = 50 });
        reached.OnLevelChanged();
        Assert.Equal(new[] { "2575:36000000", "2576:0000000000000000" }, KharaFrames(reached));
        Assert.Contains(54u, reached.State.Completed);
    }

 /// <summary>At accept, a level row is judged on the spot: 36 ("Combat level 30") accepted at level 30 spends its points, then completes.</summary>
    [Fact]
    public async Task KharaBaseLevel_CompletesAtAcceptWhenAlreadyMet()
    {
        var catalog = KharaCatalog.For(await LoadStoreAsync("Khara_Info.json"));
        var engine = new KharaEngine(catalog, new KharaStateDocument { Points = 100 }, new KharaFacts { Level = 30 });
        Assert.True(engine.Accept(36));
        Assert.Equal(new[] { "2502:5a000000", "2575:24000000", "2576:0000000000000000" }, KharaFrames(engine));
    }

 /// <summary>"Profession Level 10 achieved" stays accepted at job level 7 and completes once Character.JobLevel reaches 10.</summary>
    [Fact]
    public async Task KharaProfessionLevel_21_CompletesAtJobLevel10()
    {
        var catalog = KharaCatalog.For(await LoadStoreAsync("Khara_Info.json"));
        var low = new KharaEngine(catalog, Holding(21, 0), new KharaFacts { Level = 30, ProfessionLevel = 7 });
        low.OnLevelChanged();
        Assert.Empty(low.Frames);

        var reached = new KharaEngine(catalog, Holding(21, 0), new KharaFacts { Level = 30, ProfessionLevel = 10 });
        reached.OnLevelChanged();
        Assert.Equal(new[] { "2575:15000000", "2576:0000000000000000" }, KharaFrames(reached));
    }

 /// <summary>
 /// "Item repair number achieved" steps by the repair's item count (subject 0). Row 65 (target 5) goes from 0 to 5 on a six-item repair and then completes, sending 2575 and 2576 with 20 points. The 2577 bodies use the builder's 11-byte layout.
 /// </summary>
    [Fact]
    public async Task KharaRepairCount_StepsByTheItemCount()
    {
        var catalog = KharaCatalog.For(await LoadStoreAsync("Khara_Info.json"));
        var k79 = new KharaEngine(catalog, Holding(79, 18), new KharaFacts { Level = 40 });
        k79.OnCountedEvent(KharaService.CompleteRepairCount, 0, 6);
        Assert.Equal(new[] { "2577:4f00000000000000001800" }, KharaFrames(k79));

        var k65 = new KharaEngine(catalog, Holding(65, 0), new KharaFacts { Level = 40 });
        k65.OnCountedEvent(KharaService.CompleteRepairCount, 0, 6);
        Assert.Equal(new[] { "2577:4100000000000000000500", "2575:41000000", "2576:1400000000000000" }, KharaFrames(k65));

        var none = new KharaEngine(catalog, Holding(79, 18), new KharaFacts { Level = 40 });
        none.OnCountedEvent(KharaService.CompleteRepairCount, 0, 0);
        Assert.Empty(none.Frames);
    }

    
    
    
 /// <summary>
 /// The craft EXP goes through the same rate as gathering (EffectService.ApplyExpRate with the trait-76 sum). With no rate it is the table value. At a rate of 3,000 (every VVIP effect), a gather's 86 becomes 111.
 /// </summary>
    [Fact]
    public void ProfessionExpRate_IsTheRetailGains()
    {
        Assert.Equal(484u, EffectService.ApplyExpRate(484, 0));
        Assert.Equal("011818001be4000000004515000000000000e401000000000000",
            Hex(ProfessionExpPackets.BuildProJobExpChange2526(0x1545, EffectService.ApplyExpRate(484, 0))));
        Assert.Equal("011818001be40000000006480000000000006f00000000000000",
            Hex(ProfessionExpPackets.BuildProJobExpChange2526(0x4806, EffectService.ApplyExpRate(86, 3000))));
        Assert.Equal(629u, EffectService.ApplyExpRate(484, 3000));
    }

    
    
    
 /// <summary>
 /// Refusals: 112116650 (PC_Job 229376) asked by a job-4 character, 111010020 (PC_Job 32767) by a job-17 character, 212116750 (PC_ProJob 3) by a profession-4 job-16 character, and 111000110 (Job_Master_Quest 1, Min 30) at master level 0 are each refused with a 2431 reason code: 21718 for class, 21719 for profession, 21702 for master level. A matching class, profession or level passes.
 /// </summary>
    [Fact]
    public async Task QuestAcceptGates_AreTheArchiveRefusals()
    {
        var rows = await GateRowsAsync();
        Assert.Equal(QuestProgressPackets.QuestErrorJobMismatch, QuestAcceptGates.Refusal(rows[112116650], 4, 1, 30, 0));
        Assert.Equal("01141400c3e300000000aac3ae06d654000000000000",
            Hex(QuestProgressPackets.BuildQuestError2431(112116650, QuestProgressPackets.QuestErrorJobMismatch)));
        Assert.Null(QuestAcceptGates.Refusal(rows[112116650], 16, 1, 30, 0));

        Assert.Equal(QuestProgressPackets.QuestErrorJobMismatch, QuestAcceptGates.Refusal(rows[111010020], 17, 1, 30, 0));
        Assert.Equal("01141400c3e300000000e4e09d06d654000000000000",
            Hex(QuestProgressPackets.BuildQuestError2431(111010020, QuestProgressPackets.QuestErrorJobMismatch)));

        Assert.Equal(QuestProgressPackets.QuestErrorProJobMismatch, QuestAcceptGates.Refusal(rows[212116750], 16, 4, 30, 0));
        Assert.Equal("01141400c3e3000000000ea5a40cd754000000000000",
            Hex(QuestProgressPackets.BuildQuestError2431(212116750, QuestProgressPackets.QuestErrorProJobMismatch)));
        Assert.Null(QuestAcceptGates.Refusal(rows[212116750], 16, 3, 30, 0));
        
        Assert.Equal(QuestProgressPackets.QuestErrorJobMismatch, QuestAcceptGates.Refusal(rows[212116750], 4, 4, 30, 0));

        Assert.Equal(QuestProgressPackets.QuestErrorJobMasterLevel, QuestAcceptGates.Refusal(rows[111000110], 16, 4, 50, 0));
        Assert.Equal("01141400c3e3000000002eba9d06c654000000000000",
            Hex(QuestProgressPackets.BuildQuestError2431(111000110, QuestProgressPackets.QuestErrorJobMasterLevel)));
        Assert.Null(QuestAcceptGates.Refusal(rows[111000110], 16, 4, 50, 40));
    }

 /// <summary>
 /// The Job_Master range is checked before PC_Job. A quest that fails both gets the master-level code (21702), not the class code (21718).
 /// </summary>
    [Fact]
    public async Task QuestAcceptGates_JobMasterBeforePcJob_IsTheArchiveTwoGateFrame()
    {
        var rows = await GateRowsAsync();
        Assert.Equal(64u, rows[411000650].PC_Job);
        Assert.Equal(QuestProgressPackets.QuestErrorJobMasterLevel, QuestAcceptGates.Refusal(rows[411000650], 4, 1, 50, 0));
        Assert.Equal("01141400c3e3000000004a5f7f18c654000000000000",
            Hex(QuestProgressPackets.BuildQuestError2431(411000650, QuestProgressPackets.QuestErrorJobMasterLevel)));
        
        Assert.False(QuestAcceptGates.JobAllows(64, 4));
        Assert.True(QuestAcceptGates.JobAllows(64, 7));
    }

 /// <summary>
 /// The PC_Job bit is Job_ID - 1, and the Novice class has none. The Job_Master range of a Job_Master_Quest 0 or 2 quest is checked against the base level, Min inclusive and Max exclusive.
 /// </summary>
    [Fact]
    public async Task QuestAcceptGates_JobBitAndBaseLevelRange()
    {
        var rows = await GateRowsAsync();
        Assert.Null(QuestAcceptGates.Refusal(rows[411000078], 3, 1, 30, 0));
        Assert.Equal(QuestProgressPackets.QuestErrorJobMismatch, QuestAcceptGates.Refusal(rows[411000078], 4, 1, 30, 0));
        Assert.Equal(QuestProgressPackets.QuestErrorJobMismatch, QuestAcceptGates.Refusal(rows[411000078], 100, 1, 30, 0));

        var capped = rows.Values.First(r => r.Job_Master_Quest == 0 && r.Job_Master_Max_Level == 50
            && r.Job_Master_Min_Level is > 0 and < 50 && r.PC_Job == 0 && r.PC_ProJob == 0);
        Assert.Null(QuestAcceptGates.Refusal(capped, 1, 1, 49, 0));
        Assert.Null(QuestAcceptGates.Refusal(capped, 1, 1, capped.Job_Master_Min_Level, 0));
        Assert.Equal(QuestProgressPackets.QuestErrorJobMasterLevel, QuestAcceptGates.Refusal(capped, 1, 1, 50, 0));
        Assert.Equal(QuestProgressPackets.QuestErrorJobMasterLevel,
            QuestAcceptGates.Refusal(capped, 1, 1, (byte)(capped.Job_Master_Min_Level - 1), 0));

        var open = rows.Values.First(r => r.Job_Master_Min_Level == 0 && r.Job_Master_Max_Level == 0 && r.PC_Job == 0 && r.PC_ProJob == 0);
        Assert.Null(QuestAcceptGates.Refusal(open, 1, 1, 50, 0));
    }

 /// <summary>
 /// A Start_Type 2 quest needs its start item in the bag. Without it the accept is refused with 2421 result 20036.
 /// </summary>
    [Fact]
    public async Task QuestStartItem_IsTheArchive2421Result20036()
    {
        var store = await LoadStoreAsync("Quest_Info.json");
        Assert.Equal(40001232u, QuestAcceptGates.StartItemOf(store.Quests[111150006]));
        Assert.Equal(0u, QuestAcceptGates.StartItemOf(store.Quests[111000110]));   
        Assert.Equal("01101000b5e300000000b603a006444e0000",
            Hex(QuestProgressPackets.BuildQuestInsertResult2421(111150006, QuestProgressPackets.QuestInsertStartItemMissing)));
    }

    
    
    
 /// <summary>
 /// End to end: the 1378 talk of a quest whose talk NPC has not been met yet, at its Reward NPC, is answered 2431 21712 and then 2431 21708, and the quest stays held with nothing paid. The quest was accepted in the same login.
 /// </summary>
    [Fact]
    public async Task QuestPrize_TalkMetElsewhere_IsTheArchive21712_21708()
    {
        await using var net = await Harness.StartAsync(1);
        var players = new PlayerStateManager(new NoDirtySink());
        var broadcast = new BroadcastService(players, NullLogger<BroadcastService>.Instance);
        broadcast.SetServer(net.Server);
        var player = new PlayerState { SessionId = net.Sessions[0].SessionId, EntityId = 0x07052A11, CharacterId = 702, Name = "Talk",
            MapId = 1100, MapLoaded = true };
        player.ActiveQuests.Add(111130070);
        QuestTalkProgress.Begin(player, 111130070);   
        players.AddPlayer(player.SessionId, player);
        var collection = new ServiceCollection();
        collection.AddSingleton(await LoadStoreAsync("Quest_Info.json", "ItemInfo.json", "NPCInfo.json", "ZoneSpawn.json"));
        collection.AddSingleton<IInventoryRepository>(new BagInventory([]));
        collection.AddSingleton<IQuestRepository>(new NoQuests());
        await using var services = collection.BuildServiceProvider();
        var handler = new QuestPrizeHandler(services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<QuestPrizeHandler>.Instance, players, broadcast);

        await handler.HandleAsync(net.Sessions[0], Convert.FromHexString("01141400bee3112a050780a5ce01d6b59f0600000000"));
        await net.Sessions[0].SendInternalRmiAsync(ZoneS2COpcodes.OnLevelUp, new byte[] { 0x5A });

        Assert.Equal((ZoneS2COpcodes.OnQuestError, "01141400c3e300000000d6b59f06d054000000000000"), Hexed(await net.Readers[0].NextAsync()));
        Assert.Equal((ZoneS2COpcodes.OnQuestError, "01141400c3e300000000d6b59f06cc54000000000000"), Hexed(await net.Readers[0].NextAsync()));
        Assert.Equal((ZoneS2COpcodes.OnLevelUp, "5a"), Hexed(await net.Readers[0].NextAsync()));
        Assert.Contains(111130070u, player.ActiveQuests);
        Assert.False(player.QuestKillProgress.ContainsKey(111130070));
    }

 /// <summary>
 /// The side-close: holding quest 059 and turning in quest 060 at the talk NPC also closes 059, sending 2432 and 2427 for 059 before the 2429 and 2435 of 060. The later turn-in of 059 at its own Reward NPC is then paid, with no 2432 or 2427. Without the side-close the same turn-in would be refused.
 /// </summary>
    [Fact]
    public async Task QuestPrize_SideClose_IsTheArchiveBurst()
    {
        var store = await LoadStoreAsync("Quest_Info.json", "NPCInfo.json", "ZoneSpawn.json");
        var player = new PlayerState();
        player.ActiveQuests.Add(123000059);
        player.ActiveQuests.Add(123000060);

        Assert.Equal(new uint[] { 123000059 }, QuestPrizeHandler.SideClosedBy(player, 123000060, 11004275, store));
        Assert.Empty(QuestPrizeHandler.SideClosedBy(player, 123000060, 11004261, store));
        Assert.Equal("01131300bbe300000000fbd454070173e9a7000100", Hex(QuestNotifyPackets.BuildQuestUpdateNotify2432(123000059, 11004275)));
        Assert.Equal("01131300bbe300000000fcd454070173e9a7000100", Hex(QuestNotifyPackets.BuildQuestUpdateNotify2432(123000060, 11004275)));
        Assert.Equal("01101000bce300000000fbd4540700000000", Hex(QuestProgressPackets.BuildQuestComplete2427(123000059)));

        var q059 = store.Quests[123000059];
        Assert.True(QuestPrizeHandler.TalkMetElsewhereUnmet(q059, 11004261, objectiveClosed: false, acceptedThisLogin: true, store));
        Assert.False(QuestPrizeHandler.TalkMetElsewhereUnmet(q059, 11004261, objectiveClosed: true, acceptedThisLogin: true, store));
        Assert.False(QuestPrizeHandler.TalkMetElsewhereUnmet(q059, 11004275, objectiveClosed: false, acceptedThisLogin: true, store));   
        Assert.False(QuestPrizeHandler.TalkMetElsewhereUnmet(store.Quests[123000060], 11004275, objectiveClosed: false,
            acceptedThisLogin: true, store));

        player.QuestKillProgress[123000059] = 1;   
        Assert.Empty(QuestPrizeHandler.SideClosedBy(player, 123000060, 11004275, store));
    }

 /// <summary>
 /// The refusal's limits: a talk-met-elsewhere quest carried over a relog is not refused, since this server does not keep its close. A talk Value1 that no NPC can be met as is not refused either, and such quests are paid as before.
 /// </summary>
    [Fact]
    public async Task QuestPrize_TalkMetElsewhere_OnlyForAMeetableNpcInARunBegunThisLogin()
    {
        var store = await LoadStoreAsync("Quest_Info.json", "NPCInfo.json", "ZoneSpawn.json");
        Assert.True(QuestTalkProgress.IsMeetableNpc(store, 30210000));
        Assert.True(QuestTalkProgress.IsMeetableNpc(store, 11004275));
        Assert.False(QuestTalkProgress.IsMeetableNpc(store, 40001050));
        Assert.False(QuestTalkProgress.IsMeetableNpc(store, 99320001));
        Assert.False(QuestTalkProgress.IsMeetableNpc(store, 0));

        var q070 = store.Quests[111130070];
        Assert.True(QuestPrizeHandler.TalkMetElsewhereUnmet(q070, 30320000, objectiveClosed: false, acceptedThisLogin: true, store));
        Assert.False(QuestPrizeHandler.TalkMetElsewhereUnmet(q070, 30320000, objectiveClosed: false, acceptedThisLogin: false, store));
        var q090 = store.Quests[911000090];
        Assert.False(QuestPrizeHandler.TalkMetElsewhereUnmet(q090, q090.Reward_NPC_ID, objectiveClosed: false, acceptedThisLogin: true, store));
        var q030 = store.Quests[912000030];
        Assert.False(QuestPrizeHandler.TalkMetElsewhereUnmet(q030, q030.Reward_NPC_ID, objectiveClosed: false, acceptedThisLogin: true, store));

        var player = new PlayerState();
        player.ActiveQuests.Add(111130070);   
        Assert.False(QuestTalkProgress.AcceptedThisLogin(player, 111130070));
        QuestAcceptHandler.BeginRun(player, 111130070);
        Assert.True(QuestTalkProgress.AcceptedThisLogin(player, 111130070));
        Assert.False(QuestTalkProgress.AcceptedThisLogin(new PlayerState(), 111130070));   
    }

 /// <summary>A 1698 at the talk NPC of a single-row talk quest met elsewhere closes it. A talk row naming the Reward NPC, a Value3-1 row and a multi-objective quest are not tracked.</summary>
    [Fact]
    public async Task QuestTalkProgress_TracksTheSingleRowTalkQuestsMetElsewhere()
    {
        var store = await LoadStoreAsync("Quest_Info.json");
        Assert.Equal(new[] { 0 }, QuestTalkProgress.TrackedRows(store.QuestRows[123000059]));
        Assert.Empty(QuestTalkProgress.TrackedRows(store.QuestRows[123000060]));   
        Assert.Empty(QuestTalkProgress.TrackedRows(store.QuestRows[111150017]));   
        Assert.Empty(QuestTalkProgress.TrackedRows(store.QuestRows[411210330]));   
        Assert.Equal(4, QuestTalkProgress.TrackedRows(store.QuestRows[112210011]).Length);

        var player = new PlayerState();
        player.ActiveQuests.Add(123000059);
        Assert.True(QuestTalkProgress.Meet(player, 123000059, store.QuestRows[123000059], 11004275)!.Value.AllMet);
        Assert.Null(QuestTalkProgress.Meet(player, 123000059, store.QuestRows[123000059], 11004261));
    }

    
    private static KharaStateDocument Holding(uint kharaId, uint count)
    {
        var state = new KharaStateDocument();
        state.Active.Add(new KharaActiveDocument { KharaId = kharaId, State = KharaService.StateInProgress, Counts = [count] });
        return state;
    }

    private static string[] KharaFrames(KharaEngine engine) =>
        engine.Frames.Select(f => $"{f.Opcode}:{Hex(f.Body)}").ToArray();

    private static async Task<IReadOnlyDictionary<uint, QuestAcceptGates.Row>> GateRowsAsync()
    {
        var all = System.Text.Json.JsonSerializer.Deserialize<List<QuestAcceptGates.Row>>(
            await File.ReadAllBytesAsync(Path.Combine(RequireDataDirectory(), "Quest_Info.json")))!;
        return QuestAcceptGates.Index(all);
    }

    private static (ushort, string) Hexed((ushort Op, byte[] Body) frame) => (frame.Op, Hex(frame.Body));

    private static string RequireDataDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "data");
            if (File.Exists(Path.Combine(candidate, "Khara_Info.json")) && File.Exists(Path.Combine(candidate, "Quest_Info.json")))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("no ancestor of " + AppContext.BaseDirectory + " holds data/Khara_Info.json");
    }

 /// <summary>A GameDataStore holding only the named shipped tables (copied to a temp folder).</summary>
    private static async Task<GameDataStore> LoadStoreAsync(params string[] tables)
    {
        var source = RequireDataDirectory();
        var dir = Path.Combine(Path.GetTempPath(), "ro2-reaudit2-a1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var store = new GameDataStore();
        try
        {
            foreach (var t in tables) File.Copy(Path.Combine(source, t), Path.Combine(dir, t));
            await store.LoadAsync(dir);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
        return store;
    }

    private sealed class NoDirtySink : IDirtySink
    {
        public void Mark(PlayerState player) { }
        public void Forget(uint characterId) { }
    }

    private sealed class BagInventory(InventoryItem[] items) : IInventoryRepository
    {
        public List<InventoryItem> Items { get; } = items.ToList();
        public Task<IReadOnlyList<InventoryItem>> GetByCharacterAsync(uint characterId) =>
            Task.FromResult<IReadOnlyList<InventoryItem>>(Items.Where(i => i.CharacterId == characterId).ToList());
        public Task<IReadOnlyList<InventoryItem>> GetEquipmentAsync(uint characterId) =>
            Task.FromResult<IReadOnlyList<InventoryItem>>(Items.Where(i => i.CharacterId == characterId && i.StorageType == 1).ToList());
        public Task<InventoryItem?> GetByIdAsync(ulong id) => Task.FromResult(Items.FirstOrDefault(i => i.Id == id));
        public Task AddItemAsync(InventoryItem item) { Items.Add(item); return Task.CompletedTask; }
        public Task RemoveItemAsync(ulong itemId) { Items.RemoveAll(i => i.Id == itemId); return Task.CompletedTask; }
        public Task UpdateItemAsync(InventoryItem item) => Task.CompletedTask;
        public Task<ulong> GetNextIdAsync() => Task.FromResult(Items.Count == 0 ? 1UL : Items.Max(i => i.Id) + 1);
        public Task<int> RepairSlotsAsync(uint characterId) => Task.FromResult(0);
    }

    private sealed class NoQuests : IQuestRepository
    {
        public Task<IReadOnlyList<CharacterQuest>> GetActiveQuestsAsync(uint characterId) =>
            Task.FromResult<IReadOnlyList<CharacterQuest>>(Array.Empty<CharacterQuest>());
        public Task<IReadOnlyList<uint>> GetCompletedQuestIdsAsync(uint characterId) =>
            Task.FromResult<IReadOnlyList<uint>>(Array.Empty<uint>());
        public Task<DateTime?> GetLastCompletedAtAsync(uint characterId, uint questId) => Task.FromResult<DateTime?>(null);
        public Task AcceptQuestAsync(uint characterId, uint questId) => Task.CompletedTask;
        public Task CompleteQuestAsync(uint characterId, uint questId) => Task.CompletedTask;
        public Task GiveUpQuestAsync(uint characterId, uint questId) => Task.CompletedTask;
        public Task SetQuestsTracedAsync(uint characterId, IReadOnlyCollection<uint> tracedQuestIds) => Task.CompletedTask;
        public Task SaveProgressAsync(uint characterId, uint questId, string? progressJson) => Task.CompletedTask;
    }

 /// <summary>A loopback ProudServer with N connected clients, each socket known to be its session's.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        public required ProudServer Server { get; init; }
        public required List<Socket> Clients { get; init; }
        public required List<ProudSession> Sessions { get; init; }
        public required List<FrameReader> Readers { get; init; }

        public static async Task<Harness> StartAsync(int count)
        {
            var server = new ProudServer("test", NullLogger.Instance);
            var next = new TaskCompletionSource<ProudSession>(TaskCreationOptions.RunContinuationsAsynchronously);
            server.SessionConnected += s => { Volatile.Read(ref next).TrySetResult(s); return Task.CompletedTask; };
            await server.StartAsync(new IPEndPoint(IPAddress.Loopback, 0));
            var clients = new List<Socket>();
            var sessions = new List<ProudSession>();
            for (var i = 0; i < count; i++)
            {
                var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                await client.ConnectAsync(server.LocalEndPoint!);
                sessions.Add(await next.Task.WaitAsync(Generous));
                clients.Add(client);
                Volatile.Write(ref next, new TaskCompletionSource<ProudSession>(TaskCreationOptions.RunContinuationsAsynchronously));
            }
            return new Harness { Server = server, Clients = clients, Sessions = sessions,
                Readers = clients.Select(c => new FrameReader(c)).ToList() };
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var c in Clients) c.Dispose();
            await Server.StopAsync();
        }
    }

 /// <summary>Reads the internal RMI frames (<c>[13 57][w][len][01][u16 op][body]</c>) a test client receives.</summary>
    private sealed class FrameReader(Socket socket)
    {
        public async Task<(ushort Op, byte[] Body)> NextAsync()
        {
            while (true)
            {
                var head = await ReadExactAsync(3);
                Assert.Equal(0x13, head[0]);
                Assert.Equal(0x57, head[1]);
                var lengthBytes = await ReadExactAsync(head[2]);
                int length = head[2] switch
                {
                    1 => lengthBytes[0],
                    2 => BinaryPrimitives.ReadUInt16LittleEndian(lengthBytes),
                    _ => BinaryPrimitives.ReadInt32LittleEndian(lengthBytes),
                };
                var data = await ReadExactAsync(length);
                if (data.Length >= 3 && data[0] == 0x01)
                    return (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(1)), data[3..]);
            }
        }

        private async Task<byte[]> ReadExactAsync(int count)
        {
            var buffer = new byte[count];
            int read = 0;
            while (read < count)
            {
                int n = await socket.ReceiveAsync(buffer.AsMemory(read), SocketFlags.None).AsTask().WaitAsync(Generous);
                if (n == 0) throw new EndOfStreamException("connection closed mid-frame");
                read += n;
            }
            return buffer;
        }
    }
}
