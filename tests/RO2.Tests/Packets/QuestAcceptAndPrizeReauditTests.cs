using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.GameData;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Net;
using System.Text;
using Xunit;
using static RO2.Tests.Packets.ReauditShared;
using RO2.Inventory.Domain;

namespace RO2.Tests.Packets;

/// <summary>
/// Quest accept and prize rules: the daily gate and refusals (1374, 2421), the level grant, bag-full and
/// completion-item rules (1378), the prize bag check, and the u16 count of 2425. Expected frames use the wire layout
/// of each response. Shipped tables are found by walking up from the test output directory.
/// </summary>
public class QuestAcceptAndPrizeReauditTests
{
    
 /// <summary>Refusal responses to request 1374: 2431 with flag 6, and 2421 with result 20020 or 20021. Accepting the quest keeps result 0.</summary>
    [Fact]
    public void QuestAcceptRefusals_AreTheArchiveFrames()
    {
        Assert.Equal("01141400c3e30000000044c54c36d554000006000000",
            Hex(QuestProgressPackets.BuildQuestError2431(911000900, QuestProgressPackets.QuestErrorDailyDone, 6)));
        Assert.Equal("01101000b5e30000000007d55407344e0000",
            Hex(QuestProgressPackets.BuildQuestInsertResult2421(123000071, QuestProgressPackets.QuestErrorBagFull)));
        Assert.Equal("01101000b5e3000000002247b20c354e0000",
            Hex(QuestProgressPackets.BuildQuestInsertResult2421(213010210, QuestProgressPackets.QuestInsertItemMissing)));
        Assert.Equal("01101000b5e30000000010d5540700000000", Hex(QuestProgressPackets.BuildQuestInsert2421(123000080)));
    }

 /// <summary>
 /// The daily reset is at 13:00 UTC. A quest can be accepted again after the reset, not after a fixed 24-hour cooldown.
 /// </summary>
    [Fact]
    public void DailyGate_ResetsAt1300Utc()
    {
        static DateTime U(string s) => DateTime.SpecifyKind(DateTime.Parse(s, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc);
        Assert.True(QuestRepeatGate.DoneToday(U("2026-07-09 22:10:10"), U("2026-07-10 12:02:17")));
        Assert.False(QuestRepeatGate.DoneToday(U("2026-07-16 17:17:40"), U("2026-07-17 13:00:02")));
        Assert.True(QuestRepeatGate.DoneToday(U("2026-07-20 00:28:29"), U("2026-07-20 00:29:03")));
        Assert.False(QuestRepeatGate.DoneToday(U("2026-07-20 00:28:29"), U("2026-07-20 21:19:51")));
        Assert.False(QuestRepeatGate.DoneToday(null, U("2026-07-20 21:19:51")));
        Assert.Equal(U("2026-07-19 13:00:00"), QuestRepeatGate.LatestDailyReset(U("2026-07-20 12:59:59")));
        Assert.Equal(U("2026-07-20 13:00:00"), QuestRepeatGate.LatestDailyReset(U("2026-07-20 13:00:00")));
    }

 /// <summary>
 /// End to end for a full bag: accepting quest 123000071, which grants item 70006007, is answered with 2421 result 20020
 /// alone, and the quest is not held.
 /// </summary>
    [Fact]
    public async Task QuestAccept_WithAFullBag_IsRetails2421Result20020()
    {
        await using var net = await Harness.StartAsync(1);
        var players = new PlayerStateManager(new NoDirtySink());
        var broadcast = new BroadcastService(players, NullLogger<BroadcastService>.Instance);
        broadcast.SetServer(net.Server);
        var player = new PlayerState { SessionId = net.Sessions[0].SessionId, EntityId = 0x07087E11, CharacterId = 701, Name = "Full",
            MapId = 1100, MapLoaded = true };
        players.AddPlayer(player.SessionId, player);
        var collection = new ServiceCollection();
        collection.AddSingleton(await LoadStoreAsync("Quest_Info.json", "ItemInfo.json"));
        collection.AddSingleton<IInventoryRepository>(new GearInventory(FullBag(701)));
        collection.AddSingleton<IQuestRepository>(new NoQuests());
        await using var services = collection.BuildServiceProvider();
        var handler = new QuestAcceptHandler(services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<QuestAcceptHandler>.Instance, players, broadcast);

        await handler.HandleAsync(net.Sessions[0], Convert.FromHexString("01101000b4e3117e080707d55407500e2d01"));
        await net.Sessions[0].SendInternalRmiAsync(ZoneS2COpcodes.OnLevelUp, new byte[] { 0x5A });

        Assert.Equal((ZoneS2COpcodes.OnQuestInsert, "01101000b5e30000000007d55407344e0000"), Hexed(await net.Readers[0].NextAsync()));
        Assert.Equal((ZoneS2COpcodes.OnLevelUp, "5a"), Hexed(await net.Readers[0].NextAsync()));
        Assert.DoesNotContain(123000071u, player.ActiveQuests);
    }

 /// <summary>
 /// Take rows: an Active_Type 1 / Action_Type 4 row naming an item is an accept take. An Active_Type 2 row is a completion
 /// take unless it names the quest's own deliver item. Rows naming the quest itself take nothing.
 /// </summary>
    [Fact]
    public async Task QuestTakeRows_FollowTheShippedTable()
    {
        var store = await LoadStoreAsync("Quest_Info.json", "ItemInfo.json");
        var all = System.Text.Json.JsonSerializer.Deserialize<List<QuestAcceptHandler.QuestActionRow>>(
            await File.ReadAllBytesAsync(Path.Combine(RequireDataDirectory(), "Quest_Info.json")))!;
        QuestAcceptHandler.QuestActionRow[] Rows(uint q) => all.Where(r => r.Quest_ID == q).ToArray();
        QuestData[] Objectives(uint q) => store.QuestRows[q];

        Assert.Equal(new[] { (40000981u, 1), (40000974u, 1) }, QuestAcceptHandler.SelectTakeRows(Rows(213010290), 1, store));
        Assert.Equal(new[] { (40000971u, 1) }, QuestAcceptHandler.SelectCompletionTakes(Rows(213010110), Objectives(213010110), store));
        Assert.Empty(QuestAcceptHandler.SelectCompletionTakes(Rows(213010100), Objectives(213010100), store)); 
        Assert.Empty(QuestAcceptHandler.SelectCompletionTakes(Rows(311110010), Objectives(311110010), store)); 
    }

    
 /// <summary>
 /// End to end for a full bag on a talk-quest turn-in (quest 123000080, reward 60000988): the responses 2432, 2427,
 /// 2431 (20020) and 2431 (21708) are sent and nothing is paid. The quest stays held with its talk objective closed, so
 /// a later turn-in does not send 2427 again.
 /// </summary>
    [Fact]
    public async Task QuestPrize_WithAFullBag_IsRetails2432_2427_20020_21708()
    {
        await using var net = await Harness.StartAsync(1);
        var players = new PlayerStateManager(new NoDirtySink());
        var broadcast = new BroadcastService(players, NullLogger<BroadcastService>.Instance);
        broadcast.SetServer(net.Server);
        var player = new PlayerState { SessionId = net.Sessions[0].SessionId, EntityId = 0x07087E11, CharacterId = 701, Name = "Full",
            MapId = 1100, MapLoaded = true };
        player.ActiveQuests.Add(123000080);
        players.AddPlayer(player.SessionId, player);
        var collection = new ServiceCollection();
        collection.AddSingleton(await LoadStoreAsync("Quest_Info.json", "ItemInfo.json"));
        var bag = new GearInventory(FullBag(701));
        collection.AddSingleton<IInventoryRepository>(bag);
        collection.AddSingleton<IQuestRepository>(new NoQuests());
        await using var services = collection.BuildServiceProvider();
        var handler = new QuestPrizeHandler(services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<QuestPrizeHandler>.Instance, players, broadcast);

        await handler.HandleAsync(net.Sessions[0], Convert.FromHexString("01141400bee3117e080769e9a70010d55407ffffffff"));
        await net.Sessions[0].SendInternalRmiAsync(ZoneS2COpcodes.OnLevelUp, new byte[] { 0x5A });

        Assert.Equal((ZoneS2COpcodes.OnQuestUpdateNotify, "01131300bbe30000000010d554070169e9a7000100"), Hexed(await net.Readers[0].NextAsync()));
        Assert.Equal((ZoneS2COpcodes.OnQuestCompleteNotify, "01101000bce30000000010d5540700000000"), Hexed(await net.Readers[0].NextAsync()));
        Assert.Equal((ZoneS2COpcodes.OnQuestError, "01141400c3e30000000010d55407344e000000000000"), Hexed(await net.Readers[0].NextAsync()));
        Assert.Equal((ZoneS2COpcodes.OnQuestError, "01141400c3e30000000010d55407cc54000000000000"), Hexed(await net.Readers[0].NextAsync()));
        Assert.Equal((ZoneS2COpcodes.OnLevelUp, "5a"), Hexed(await net.Readers[0].NextAsync()));
        Assert.Contains(123000080u, player.ActiveQuests);
        Assert.Equal((ushort)1, player.QuestKillProgress[123000080]);
        Assert.Equal(20, bag.Items.Count);
    }

 /// <summary>
 /// The Start_Type 4 level grant: quest 911010020 is granted at base level 25 to 50 with job level 1 or higher, never
 /// below level 25. Quest 911010030 is not granted at base level 50 with job level 1, only from job level 50.
 /// </summary>
    [Fact]
    public async Task LevelGrant_MeetsTheShippedStartType4Rows()
    {
        var store = await LoadStoreAsync("Quest_Info.json");
        var proJobs = System.Text.Json.JsonSerializer.Deserialize<List<QuestLevelGrants.ProJobRow>>(
            await File.ReadAllBytesAsync(Path.Combine(RequireDataDirectory(), "Quest_Info.json")))!
            .GroupBy(r => r.Quest_ID).ToDictionary(g => g.Key, g => g.First());
        var first = store.Quests[911010020];
        var second = store.Quests[911010030];
        Assert.True(QuestLevelGrants.Meets(first, proJobs[911010020], 25, 1));
        Assert.True(QuestLevelGrants.Meets(first, proJobs[911010020], 28, 3));
        Assert.False(QuestLevelGrants.Meets(first, proJobs[911010020], 24, 1));
        Assert.False(QuestLevelGrants.Meets(first, proJobs[911010020], 51, 1));
        Assert.False(QuestLevelGrants.Meets(first, proJobs[911010020], 25, 0));
        Assert.False(QuestLevelGrants.Meets(second, proJobs[911010030], 50, 1));
        Assert.True(QuestLevelGrants.Meets(second, proJobs[911010030], 50, 50));
        Assert.Equal(new uint[] { 911010020, 911010030 },
            store.Quests.Values.Where(q => q.Start_Type == QuestLevelGrants.LevelStartType).Select(q => q.Quest_ID).Order().ToArray());
    }

 /// <summary>The reward items a turn-in needs cells for, by Reward_Type, and the stacks a hand-over empties.</summary>
    [Fact]
    public void PrizeBagCheck_CountsRewardsAndEmptiedStacks()
    {
        var all = new QuestData { Quest_ID = 1, Reward_Type = 1, Reward_Item_ID1 = 11, Reward_Item_Count1 = 1, Reward_Item_ID2 = 12, Reward_Item_Count2 = 2 };
        Assert.Equal(new uint[] { 11, 12 }, QuestPrizeHandler.RewardItemsOf(all, QuestPrizeHandler.NoRewardChoice).Where(r => r.Id != 0).Select(r => r.Id).ToArray());
        var choice = all with { Reward_Type = 2 };
        Assert.Equal(new uint[] { 12 }, QuestPrizeHandler.RewardItemsOf(choice, 1).Select(r => r.Id).ToArray());
        Assert.Empty(QuestPrizeHandler.RewardItemsOf(choice, QuestPrizeHandler.NoRewardChoice));
        Assert.Empty(QuestPrizeHandler.RewardItemsOf(all with { Reward_Type = 0 }, 0));

        InventoryItem[] bag =
        [
            new() { Id = 1, CharacterId = 1, ItemId = 40001226, StorageType = 0, Slot = 0, Count = 2 },
            new() { Id = 2, CharacterId = 1, ItemId = 40001226, StorageType = 0, Slot = 1, Count = 5 },
            new() { Id = 3, CharacterId = 1, ItemId = 40001226, StorageType = 1, Slot = 2, Count = 9 },
        ];
        Assert.Equal(new ulong[] { 1 }, QuestPrizeHandler.StacksEmptiedBy(bag, 40001226, 3).ToArray());
        Assert.Equal(new ulong[] { 1, 2 }, QuestPrizeHandler.StacksEmptiedBy(bag, 40001226, 7).Order().ToArray());
    }

    
 /// <summary>
 /// The count is written as a u16: 1 is unchanged, and 300 is sent as bytes 2c 01.
 /// </summary>
    [Fact]
    public void QuestMonsterOrGo2425_CountIsAU16()
    {
        Assert.Equal("01121200dbe300000000d4aa3c266efdfa020100", Hex(QuestProgressPackets.BuildQuestMonsterOrGo2425(0x263CAAD4, 0x02FAFD6E, 1)));
        Assert.EndsWith("6efdfa022c01", Hex(QuestProgressPackets.BuildQuestMonsterOrGo2425(0x263CAAD4, 0x02FAFD6E, 300)));
    }

 /// <summary>
 /// Quest 211112314 has two kill rows of 1,000 each. The counters hold values up to 1,000: 1,000 and 1,000 complete the
 /// quest, 1,000 and 255 do not.
 /// </summary>
    [Fact]
    public async Task KillCounters_CountTo1000()
    {
        var store = await LoadStoreAsync("Quest_Info.json");
        var rows = store.QuestRows[211112314];
        Assert.Equal(new uint[] { 1000, 1000 }, rows.Where(r => r.Complete_Type == 2).Select(r => r.Complete_Type_Value2).ToArray());
        var player = new PlayerState();
        player.ActiveQuests.Add(211112314);
        LootService.RestoreKillProgress(player, 211112314, rows, new ushort[] { 1000, 255 });
        Assert.False(LootService.AllKillObjectivesMet(player, 211112314, rows));
        Assert.Equal((ushort)1000, player.QuestKillProgress[211112314]);
        LootService.RestoreKillProgress(player, 211112314, rows, new ushort[] { 1000, 1000 });
        Assert.True(LootService.AllKillObjectivesMet(player, 211112314, rows));
        Assert.Equal(new ushort[] { 1000, 1000 }, QuestProgressStore.CurrentKillCounts(player, 211112314, rows));
    }

 /// <summary>Twenty bag items in the main bag's 20 cells: no free cell.</summary>
    private static InventoryItem[] FullBag(uint characterId) => Enumerable.Range(0, 20)
        .Select(i => new InventoryItem { Id = (ulong)(9000 + i), CharacterId = characterId, ItemId = 70006005, StorageType = 0, Slot = (short)i, Count = 1 })
        .ToArray();

 /// <summary>A character with no quest documents.</summary>
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
}
