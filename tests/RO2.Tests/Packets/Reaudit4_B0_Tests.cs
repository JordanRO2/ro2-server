using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Network.Packets;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Tests for the 2498 rate slots (PcTraitRates), the stats of worn Khara titles on every sheet and in the 2498 of an accepted 1438 (KharaTitleTraits, KharaTitleSelectHandler.AnswerAsync), and the quest objective closes kept across a relog (QuestProgressStore).
/// </summary>
public sealed class Reaudit4_B0_Tests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);
    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();
    private static ushort U16(byte[] body, int blobOffset) => BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(3 + blobOffset));
    private static uint U32(byte[] body, int blobOffset) => BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(3 + blobOffset));

    private static readonly Lazy<Task<GameDataStore>> SheetStore = new(() => LoadStoreAsync(
        "Khara_Info.json", "KHARA_Title_Info.json", "TraitInfo.json", "PCBaseStatus.json"));

    private static readonly Lazy<Task<GameDataStore>> QuestStore = new(() => LoadStoreAsync(
        "Quest_Info.json", "ItemInfo.json", "NPCInfo.json", "ZoneSpawn.json"));

    
    
    
    
    
    private const string RetailLeff50 =
        "028c008c00cce300000000a52c00002e0c74020e018a01bc00b00200000000000000000000000000004b00e9040000000000000000c203" +
        "000001000000230b000000000000000000004040600a00008a002b0009000000230b230bb6060f010e0174029902990200000000000000" +
        "000000000000000000000000000000000000000000000000009902000000000000";

    
    private const string RetailLeff90 =
        "028c008c00cce300000000b7540000b3060107aa00d5006700520400007701000000000000000000000000690f00000000000000000000" +
        "000001000000a3010000000000000000000040404b340000a304440005000000790da3015e12ab008e0483078f0478009d020000000000" +
        "000000000000000000000000000000000000000000fe0063037800000000000000";

    private static readonly int[] RateOffsets =
    [
        PcTraitRates.CastRateOffset, PcTraitRates.CritRateOffset, PcTraitRates.HitRateOffset,
        PcTraitRates.VigorRateOffset, PcTraitRates.AccelRateOffset, PcTraitRates.PenRateOffset,
    ];

 /// <summary>The rate slots, wiped and stamped again from their constants, come back byte for byte.</summary>
    [Theory]
    [InlineData(RetailLeff50, 50)]
    [InlineData(RetailLeff90, 90)]
    public void Rates_StampedFromTheConsts_AreRetails(string retail, int effectiveLevel)
    {
        var frame = Convert.FromHexString(retail);
        Assert.Equal(TraitPackets.PcTraitBodyLen, frame.Length);
        var wiped = (byte[])frame.Clone();
        foreach (var offset in RateOffsets)
            BinaryPrimitives.WriteUInt16LittleEndian(wiped.AsSpan(3 + offset), 0);
        Assert.NotEqual(Hex(frame), Hex(wiped));

        PcTraitRates.Stamp(wiped, effectiveLevel, null);
        Assert.Equal(Hex(frame), Hex(wiped));
    }

 /// <summary>The vigor rate R(C) = floor(100 x C / (0.005 x C + 0.2 x Leff + 10)), with R(0) = 0. Vigor stops at 7500.</summary>
    [Fact]
    public void Rates_Formula_AndTheVigorCap()
    {
        Assert.Equal(0, PcTraitRates.Rate(0, 50));
        Assert.Equal(319, PcTraitRates.Rate(50, 27));            
        var body = new byte[TraitPackets.PcTraitBodyLen];
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(3 + PcTraitRates.VigorConstOffset), 60000);
        PcTraitRates.Stamp(body, 50, null);
        Assert.Equal(PcTraitRates.VigorRateCap, U16(body, PcTraitRates.VigorRateOffset));
    }

 /// <summary>
 /// The in-world sheet with effects: the Trait_Kind 3 rate lines land in their slots after the fold. Crit (+60) and hit (+62) are the sums of their lines alone, and the cast rate is R(const) plus its line. Effect ids are not needed here, since the lines are already summed.
 /// </summary>
    [Fact]
    public async Task Rates_KindThreeLines_AreAddedAfterTheEffectFold()
    {
        var store = await SheetStore.Value;
        var c = Hero(4_400_001);
        var modifiers = new EffectService.EffectSheetModifiers(
            new Dictionary<byte, double> { [33] = 1000, [34] = 700, [32] = 500, [56] = 200 },
            new Dictionary<byte, double>());
        var body = EffectService.BuildPcTraitSheet(c, store, StatService.EquipStats.Empty, 100, 10, modifiers);
        Assert.Equal(1000, U16(body, PcTraitRates.CritRateOffset));
        Assert.Equal(700, U16(body, PcTraitRates.HitRateOffset));
        Assert.Equal(500, U16(body, PcTraitRates.CastRateOffset));     
        Assert.Equal(200, U16(body, PcTraitRates.VigorRateOffset));
    }

 /// <summary>The world-entry sheet stamps the rates from its own constants at the character's level, with no effect line: a penetration constant of 50 from gear at level 27 gives 319.</summary>
    [Fact]
    public async Task Rates_GearSheet_StampsThePenRate()
    {
        var store = await SheetStore.Value;
        var c = Hero(4_400_002);
        c.Level = 27;
        var gear = new StatService.EquipStats(new Dictionary<byte, int> { [94] = 50 }, new Dictionary<byte, float>());
        var donor = WorldEntryService.BuildFullPcTrait2498(c, null, null, 100, 10);
        var body = WorldEntryService.BuildPcTrait2498FromDonor(donor, c, gear, store, (100, 10));
        Assert.Equal(50, U16(body, PcTraitRates.PenConstOffset));
        Assert.Equal(319, U16(body, PcTraitRates.PenRateOffset));
        Assert.Equal(0, U16(body, PcTraitRates.CritRateOffset));
    }

    
    
    
 /// <summary>KHARA_Title_Info 203 (Khara 556) -> Option_1 -> TraitInfo: STR 80, AGI 30, INT 80, WIS 30, VIT 30.</summary>
    [Fact]
    public async Task TitleTraits_Title203_AreItsTraitInfoRows()
    {
        var store = await SheetStore.Value;
        Assert.Equal(203u, KharaTitleStore.TitleOf(556, store.KharaInfo));
        Assert.Equal(new (byte, byte, float)[] { (1, 0, 80f), (1, 1, 30f), (1, 2, 80f), (1, 3, 30f), (1, 4, 30f) },
            KharaTitleTraits.TraitsOf(store, 203).ToArray());
        Assert.Empty(KharaTitleTraits.TraitsOf(store, 0));
    }

 /// <summary>
 /// The world-entry 2498 with title 203 worn in slot B: primaries 81/31/81/31/31 (base 1 plus 80/30/80/30/30) and max SP 558 (WisSPValue 18 x WIS 31). The same primaries and max SP come out for a WisSPValue-18 class with all primaries at 1.
 /// </summary>
    [Fact]
    public async Task Sheet_WornTitle_IsInTheWorldEntryPrimaries()
    {
        var store = await SheetStore.Value;
        var retail = Convert.FromHexString(
            "028c008c00cce300000000e80900002e0251001f0051001f001f000000000000005000000000000000000000000000f30000000000" +
            "0000000001000000000000000000000000000000803fe80900002e0203000200000000000000c20020003e00510000000000000000" +
            "00000000000000000000000000000000000000000000000000000000000000000000000000");
        const uint id = 4_400_003;
        var c = Hero(id);
        c.Job = 8;
        try
        {
            KharaTitleStore.Publish(id, new KharaTitleState(0, 556, null, null), store.KharaInfo);
            var body = WorldEntryService.BuildFullPcTrait2498(c, store, Array.Empty<InventoryItem>(), 1, 1);
            for (int i = 0; i < 5; i++)
                Assert.Equal(U16(retail, TraitPackets.PcTraitPrimaryStatsOffset + 2 * i), U16(body, TraitPackets.PcTraitPrimaryStatsOffset + 2 * i));
            Assert.Equal(U16(retail, TraitPackets.PcTraitMaxSpOffset), U16(body, TraitPackets.PcTraitMaxSpOffset));
        }
        finally
        {
            KharaTitleStore.Publish(id, default, store.KharaInfo);
        }
    }

 /// <summary>
 /// Trait 21 and 22 flat rows add to the class max: title 237 grants WIS +150 and max SP +200, and title 234 grants VIT +200 and max HP +1000. A gear percent multiplies a title's flat: STR +10 % over title 203's +80 on a base of 1 gives round(81 x 1.1) = 89. The combat cache reads the same numbers.
 /// </summary>
    [Fact]
    public async Task Sheet_TitleMaxHpSpAndGearPercent_AndTheCombatCacheAgrees()
    {
        var store = await SheetStore.Value;
        const uint id = 4_400_004;
        var c = Hero(id);
        var pc = store.PcBaseStatus[c.Job];
        var bare = WorldEntryService.BuildFullPcTrait2498(c, store, Array.Empty<InventoryItem>(), 1, 1);
        try
        {
            KharaTitleStore.Publish(id, new KharaTitleState(0, 619, null, null), store.KharaInfo);
            var with237 = WorldEntryService.BuildFullPcTrait2498(c, store, Array.Empty<InventoryItem>(), 1, 1);
            Assert.Equal(pc.WisSPValue * 150 + 200, U16(with237, TraitPackets.PcTraitMaxSpOffset) - U16(bare, TraitPackets.PcTraitMaxSpOffset));

            KharaTitleStore.Publish(id, new KharaTitleState(0, 616, null, null), store.KharaInfo);
            var with234 = WorldEntryService.BuildFullPcTrait2498(c, store, Array.Empty<InventoryItem>(), 1, 1);
            Assert.Equal(pc.VitHPValue * 200 + 1000, (long)U32(with234, TraitPackets.PcTraitMaxHpOffset) - U32(bare, TraitPackets.PcTraitMaxHpOffset));
            var combat = StatService.EffectivePlayerStats(store, c, Array.Empty<InventoryItem>());
            Assert.Equal(U32(with234, TraitPackets.PcTraitMaxHpOffset), combat.MaxHp);
            Assert.Equal(1 + 200, combat.Vit);

            KharaTitleStore.Publish(id, new KharaTitleState(0, 556, null, null), store.KharaInfo);
            var gear = new StatService.EquipStats(new Dictionary<byte, int>(), new Dictionary<byte, float> { [0] = 10f });
            var donor = WorldEntryService.BuildFullPcTrait2498(c, null, null, 1, 1);
            var body = WorldEntryService.BuildPcTrait2498FromDonor(donor, c, gear, store, (1, 1));
            Assert.Equal(89, U16(body, TraitPackets.PcTraitPrimaryStatsOffset));
        }
        finally
        {
            KharaTitleStore.Publish(id, default, store.KharaInfo);
        }
    }

 /// <summary>
 /// An accepted 1438 answers 2592, then 2498 with the owner's sheet including the new title, then 2582. A refusal is the 2582 alone. The 2592 and 2582 bodies are built by KharaTitleSelectPackets, and the 2498 by the sheet builder with the title.
 /// </summary>
    [Fact]
    public async Task Select_Success_Is2592_2498_2582_AndARefusalIs2582Alone()
    {
        var store = await SheetStore.Value;
        await using var net = await Harness.StartAsync();
        const uint id = 4_400_005;
        var players = new PlayerStateManager(new NoDirtySink());
        var broadcast = new BroadcastService(players, NullLogger<BroadcastService>.Instance);
        broadcast.SetServer(net.Server);
        var c = Hero(id);
        var player = new PlayerState
        {
            SessionId = net.Session.SessionId, CharacterId = id, EntityId = 0x0785EC11u, Name = c.Name, Job = c.Job,
            Level = c.Level, MapId = 1100, MapLoaded = true, Character = c, Hp = 1, MaxHp = 1, Mp = 1, MaxMp = 1,
        };
        players.AddPlayer(player.SessionId, player);
        var collection = new ServiceCollection();
        collection.AddSingleton(new EffectService(players: null!, broadcast: null!, data: store, npcState: null!,
            loot: null!, scopeFactory: null!, logger: NullLogger<EffectService>.Instance, emblems: TestEmblems.Create()));
        collection.AddSingleton<IInventoryRepository>(new BagInventory([]));
        collection.AddSingleton(store);
        await using var services = collection.BuildServiceProvider();
        try
        {
            var accepted = new KharaTitleDecision(true, 0, KharaTitleStore.ResultOk,
                new KharaTitleState(0, 556, null, null), 0, 556);
            KharaTitleStore.Publish(id, accepted.NewState, store.KharaInfo);
            await KharaTitleSelectHandler.AnswerAsync(net.Session, broadcast, player, services, 0, 556, accepted,
                NullLogger.Instance);
            var refused = new KharaTitleDecision(false, 3453, KharaTitleStore.ResultRefused, accepted.NewState,
                KharaTitleStore.SlotNotApplied, KharaTitleStore.SlotNotApplied);
            await KharaTitleSelectHandler.AnswerAsync(net.Session, broadcast, player, services, 0, 619, refused,
                NullLogger.Instance);
            await net.Session.SendInternalRmiAsync(ZoneS2COpcodes.OnLevelUp, new byte[] { 0x5A });

            var f1 = await net.Reader.NextAsync();
            Assert.Equal((ZoneS2COpcodes.NfyKharaTitleChange, Hex(KharaTitleSelectPackets.Build2592(0x0785EC11u, accepted))), (f1.Op, Hex(f1.Body)));
            var f2 = await net.Reader.NextAsync();
            Assert.Equal(ZoneS2COpcodes.OnPcTrait, f2.Op);
            Assert.Equal(Hex(WorldEntryService.BuildFullPcTrait2498(c, store, Array.Empty<InventoryItem>(), 1, 1)), Hex(f2.Body));
            Assert.Equal(81, U16(f2.Body, TraitPackets.PcTraitPrimaryStatsOffset));      
            var f3 = await net.Reader.NextAsync();
            Assert.Equal((ZoneS2COpcodes.AnsKharaTitleSelect, Hex(KharaTitleSelectPackets.Build2582(0, 556, accepted))), (f3.Op, Hex(f3.Body)));
            var f4 = await net.Reader.NextAsync();
            Assert.Equal((ZoneS2COpcodes.AnsKharaTitleSelect, Hex(KharaTitleSelectPackets.Build2582(0, 619, refused))), (f4.Op, Hex(f4.Body)));
            var f5 = await net.Reader.NextAsync();
            Assert.Equal((ZoneS2COpcodes.OnLevelUp, "5a"), (f5.Op, Hex(f5.Body)));
            
            Assert.Equal((int)U32(f2.Body, TraitPackets.PcTraitMaxHpOffset), player.MaxHp);
        }
        finally
        {
            KharaTitleStore.Publish(id, default, store.KharaInfo);
        }
    }

    
    
    
    private static byte[] Element(CharacterQuest quest, GameDataStore data, IReadOnlyList<InventoryItem>? bag)
    {
        using var w = new PacketWriter();
        QuestProgressStore.WriteLoginElement(w, quest, data, bag);
        return w.ToArray();
    }

    private static CharacterQuest Held(uint questId, string? json = null) =>
        new() { CharacterId = 941, QuestId = questId, ProgressJson = json };

    private static InventoryItem[] Bag(uint itemId, int count, byte storageType = 0) =>
        [new InventoryItem { Id = 77, CharacterId = 941, ItemId = itemId, Count = count, StorageType = storageType, Slot = 3 }];

 /// <summary>Collect / deliver progress is the bag: min(held, Value2), state 1 at the need.</summary>
    [Theory]
    
    [InlineData(613010010u, 40001020u, 7, "01005aca892401000000000000000700000000000000000000000000000000000000")]
    
    [InlineData(613010010u, 40001020u, 1, "01005aca892402000000000000000100000000000000000000000000000000000000")]
    
    [InlineData(212210019u, 40001244u, 10, "01006311a60c02000000000000000a00000000000000000000000000000000000000")]
    
    [InlineData(123000049u, 70006005u, 1, "0100f1d4540701000000000000000100000000000000000000000000000000000000")]
    public async Task LoginElement_CollectAndDeliver_AreTheBag(uint questId, uint itemId, int held, string retail)
    {
        var data = await QuestStore.Value;
        Assert.Equal(retail, Hex(Element(Held(questId), data, Bag(itemId, held))));
        
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(Element(Held(questId), data, Bag(itemId, held, 2)).AsSpan(14)));
    }

 /// <summary>A talk or use-item close stored in an earlier login loads at state 1 with 1 at its row; unclosed, state 2.</summary>
    [Theory]
    
    [InlineData(911010020u, "{\"kill\":[0],\"met\":[1]}", "0100e4e84c3601000000000000000100000000000000000000000000000000000000")]
    
    [InlineData(111130380u, "{\"kill\":[0],\"met\":[1]}", "01000cb79f0601000000000000000100000000000000000000000000000000000000")]
    
    [InlineData(111130380u, null, "01000cb79f0602000000000000000000000000000000000000000000000000000000")]
    
    [InlineData(111150017u, "{\"kill\":[0],\"met\":[0]}", "0100c103a00602000000000000000000000000000000000000000000000000000000")]
    public async Task LoginElement_StoredTalkAndUseCloses_AreRetails(uint questId, string? json, string retail)
    {
        var data = await QuestStore.Value;
        Assert.Equal(retail, Hex(Element(Held(questId, json), data, null)));
    }

 /// <summary>The closes are stored from the session state and put back by the next login's restore.</summary>
    [Fact]
    public async Task ObjectiveState_IsStoredAndRestored()
    {
        var data = await QuestStore.Value;
        var repo = new RecordingQuests();
        var player = new PlayerState { CharacterId = 941, Name = "Relog" };
        player.ActiveQuests.Add(911010020);
        player.QuestKillProgress[911010020] = 1;                       
        await QuestProgressStore.SaveObjectiveStateAsync(repo, player, 911010020, data, NullLogger.Instance);
        Assert.Equal((941u, 911010020u, "{\"kill\":[0],\"met\":[1]}"), Assert.Single(repo.Saves));

        var next = new PlayerState { CharacterId = 941 };
        var stored = Held(911010020, repo.Saves[0].Json);
        QuestProgressStore.RestoreKillProgress(next, stored, data);
        QuestProgressStore.RestoreObjectiveState(next, stored, data, Array.Empty<InventoryItem>());
        Assert.Equal((ushort)1, next.QuestKillProgress[911010020]);
        Assert.True(QuestProgressStore.PersistedStateKnown(next, 911010020));

        
        var old = new PlayerState { CharacterId = 941 };
        QuestProgressStore.RestoreObjectiveState(old, Held(911010020, "{\"kill\":[0]}"), data, null);
        Assert.False(old.QuestKillProgress.ContainsKey(911010020));
        Assert.False(QuestProgressStore.PersistedStateKnown(old, 911010020));

        
        var collector = new PlayerState { CharacterId = 941 };
        QuestProgressStore.RestoreObjectiveState(collector, Held(613010010), data, Bag(40001020, 5));
        Assert.Equal((ushort)5, collector.QuestKillProgress[613010010]);
    }

 /// <summary>A multi-talk quest's partial meets and a multi-row use-item quest's uses survive the relog.</summary>
    [Fact]
    public async Task PartialMeetsAndUses_AreKept()
    {
        var data = await QuestStore.Value;
        var repo = new RecordingQuests();
        var rows = data.QuestRows[112210011];
        var player = new PlayerState { CharacterId = 941 };
        player.ActiveQuests.Add(112210011);
        Assert.False(QuestTalkProgress.Meet(player, 112210011, rows, 32650000)!.Value.AllMet);   
        Assert.False(QuestTalkProgress.Meet(player, 112210011, rows, 32680000)!.Value.AllMet);   
        await QuestProgressStore.SaveObjectiveStateAsync(repo, player, 112210011, data, NullLogger.Instance);
        Assert.Equal("{\"kill\":[0,0,0,0],\"met\":[1,0,1,0]}", repo.Saves[^1].Json);

        var next = new PlayerState { CharacterId = 941 };
        next.ActiveQuests.Add(112210011);
        QuestProgressStore.RestoreObjectiveState(next, Held(112210011, repo.Saves[^1].Json), data, null);
        Assert.False(next.QuestKillProgress.ContainsKey(112210011));                          
        Assert.False(QuestTalkProgress.Meet(next, 112210011, rows, 32770000)!.Value.AllMet);   
        Assert.True(QuestTalkProgress.Meet(next, 112210011, rows, 32710000)!.Value.AllMet);    

        var useRows = data.QuestRows[231010070];
        var user = new PlayerState { CharacterId = 941 };
        QuestUseItemProgress.AddUse(user, 231010070, useRows.Length, 0, 2);
        QuestUseItemProgress.AddUse(user, 231010070, useRows.Length, 3, 1);
        await QuestProgressStore.SaveObjectiveStateAsync(repo, user, 231010070, data, NullLogger.Instance);
        Assert.Equal("{\"kill\":[0,0,0,0,0],\"met\":[1,0,0,1,0]}", repo.Saves[^1].Json);
        var again = new PlayerState { CharacterId = 941 };
        QuestProgressStore.RestoreObjectiveState(again, Held(231010070, repo.Saves[^1].Json), data, null);
        Assert.Equal(1, QuestUseItemProgress.Count(again, 231010070, 0));
        Assert.Equal(1, QuestUseItemProgress.Count(again, 231010070, 3));
        Assert.False(again.QuestKillProgress.ContainsKey(231010070));
    }

 /// <summary>
 /// A talk quest closed in an earlier login (stored close, restored at login) is turned in with no 2432 or 2427. This is driven through the bag-full branch of 1378, so no payout is needed: the quest answers only 2431 20020 and 2431 21708, where an unclosed one would also answer 2432 and 2427.
 /// </summary>
    [Fact]
    public async Task TurnIn_OfARestoredClosedTalkQuest_SendsNo2432Or2427()
    {
        var data = await QuestStore.Value;
        await using var net = await Harness.StartAsync();
        var players = new PlayerStateManager(new NoDirtySink());
        var broadcast = new BroadcastService(players, NullLogger<BroadcastService>.Instance);
        broadcast.SetServer(net.Server);
        var player = new PlayerState { SessionId = net.Session.SessionId, EntityId = 0x07087E11, CharacterId = 701, Name = "Full",
            MapId = 1100, MapLoaded = true };
        var stored = new CharacterQuest { CharacterId = 701, QuestId = 123000080, ProgressJson = "{\"kill\":[0],\"met\":[1]}" };
        player.ActiveQuests.Add(123000080);
        QuestProgressStore.RestoreKillProgress(player, stored, data);
        QuestProgressStore.RestoreObjectiveState(player, stored, data, null);
        players.AddPlayer(player.SessionId, player);
        var collection = new ServiceCollection();
        collection.AddSingleton(data);
        collection.AddSingleton<IInventoryRepository>(new BagInventory(Enumerable.Range(0, 20)
            .Select(i => new InventoryItem { Id = (ulong)(9000 + i), CharacterId = 701, ItemId = 70006005, StorageType = 0, Slot = (short)i, Count = 1 })
            .ToArray()));
        var repo = new RecordingQuests();
        collection.AddSingleton<IQuestRepository>(repo);
        await using var services = collection.BuildServiceProvider();
        var handler = new QuestPrizeHandler(services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<QuestPrizeHandler>.Instance, players, broadcast);

        await handler.HandleAsync(net.Session, Convert.FromHexString("01141400bee3117e080769e9a70010d55407ffffffff"));
        await net.Session.SendInternalRmiAsync(ZoneS2COpcodes.OnLevelUp, new byte[] { 0x5A });

        var f1 = await net.Reader.NextAsync();
        Assert.Equal((ZoneS2COpcodes.OnQuestError, "01141400c3e30000000010d55407344e000000000000"), (f1.Op, Hex(f1.Body)));
        var f2 = await net.Reader.NextAsync();
        Assert.Equal((ZoneS2COpcodes.OnQuestError, "01141400c3e30000000010d55407cc54000000000000"), (f2.Op, Hex(f2.Body)));
        var f3 = await net.Reader.NextAsync();
        Assert.Equal((ZoneS2COpcodes.OnLevelUp, "5a"), (f3.Op, Hex(f3.Body)));
        Assert.Contains(123000080u, player.ActiveQuests);
        
        Assert.Equal((701u, 123000080u, "{\"kill\":[0],\"met\":[1]}"), repo.Saves[^1]);
    }

 /// <summary>
 /// The talk-elsewhere refusal (21712 and 21708) applies to a quest carried over a relog whose run state is stored, and not to one whose document predates the stored closes. Quest 123000059: talk 11004275, Reward NPC 11004261.
 /// </summary>
    [Fact]
    public async Task TalkElsewhereRefusal_FollowsTheStoredRunState()
    {
        var data = await QuestStore.Value;
        var q = data.Quests[123000059];
        var known = new PlayerState { CharacterId = 941 };
        QuestProgressStore.RestoreObjectiveState(known, Held(123000059, "{\"kill\":[0],\"met\":[0]}"), data, null);
        var unknown = new PlayerState { CharacterId = 941 };
        QuestProgressStore.RestoreObjectiveState(unknown, Held(123000059, null), data, null);

        bool Refused(PlayerState p) => QuestPrizeHandler.TalkMetElsewhereUnmet(q, 11004261, objectiveClosed: false,
            acceptedThisLogin: QuestTalkProgress.AcceptedThisLogin(p, 123000059) || QuestProgressStore.PersistedStateKnown(p, 123000059),
            data);
        Assert.True(Refused(known));
        Assert.False(Refused(unknown));
    }

    
    
    
    private static Character Hero(uint id) => new()
    {
        Id = id, Name = "Titled", Job = 1, Level = 50,
        Str = 1, Agi = 1, Int = 1, Wis = 1, Vit = 1, Luk = 0,
        Hp = 1, MaxHp = 1, Mp = 1, MaxMp = 1,
    };

    private static string RequireDataDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "data");
            if (File.Exists(Path.Combine(candidate, "KHARA_Title_Info.json")) && File.Exists(Path.Combine(candidate, "Quest_Info.json")))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("no ancestor of " + AppContext.BaseDirectory + " holds data/KHARA_Title_Info.json");
    }

 /// <summary>A GameDataStore holding only the named shipped tables (copied to a temp folder).</summary>
    private static async Task<GameDataStore> LoadStoreAsync(params string[] tables)
    {
        var source = RequireDataDirectory();
        var dir = Path.Combine(Path.GetTempPath(), "ro2-reaudit4-b0-" + Guid.NewGuid().ToString("N"));
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

 /// <summary>Records <see cref="IQuestRepository.SaveProgressAsync"/>.</summary>
    private sealed class RecordingQuests : IQuestRepository
    {
        public List<(uint CharacterId, uint QuestId, string? Json)> Saves { get; } = new();

        public Task SaveProgressAsync(uint characterId, uint questId, string? progressJson)
        {
            lock (Saves) Saves.Add((characterId, questId, progressJson));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<CharacterQuest>> GetActiveQuestsAsync(uint characterId)
            => Task.FromResult<IReadOnlyList<CharacterQuest>>(Array.Empty<CharacterQuest>());
        public Task<IReadOnlyList<uint>> GetCompletedQuestIdsAsync(uint characterId)
            => Task.FromResult<IReadOnlyList<uint>>(Array.Empty<uint>());
        public Task<DateTime?> GetLastCompletedAtAsync(uint characterId, uint questId) => Task.FromResult<DateTime?>(null);
        public Task AcceptQuestAsync(uint characterId, uint questId) => Task.CompletedTask;
        public Task CompleteQuestAsync(uint characterId, uint questId) => Task.CompletedTask;
        public Task GiveUpQuestAsync(uint characterId, uint questId) => Task.CompletedTask;
        public Task SetQuestsTracedAsync(uint characterId, IReadOnlyCollection<uint> tracedQuestIds) => Task.CompletedTask;
    }

 /// <summary>A loopback ProudServer with one connected client whose S2C RMI frames are read back.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        public required ProudServer Server { get; init; }
        public required Socket Client { get; init; }
        public required ProudSession Session { get; init; }
        public required FrameReader Reader { get; init; }

        public static async Task<Harness> StartAsync()
        {
            var server = new ProudServer("test", NullLogger.Instance);
            var connected = new TaskCompletionSource<ProudSession>(TaskCreationOptions.RunContinuationsAsynchronously);
            server.SessionConnected += s => { connected.TrySetResult(s); return Task.CompletedTask; };
            await server.StartAsync(new IPEndPoint(IPAddress.Loopback, 0));
            var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await client.ConnectAsync(server.LocalEndPoint!);
            var session = await connected.Task.WaitAsync(Generous);
            return new Harness { Server = server, Client = client, Session = session, Reader = new FrameReader(client) };
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Server.StopAsync();
        }
    }

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
