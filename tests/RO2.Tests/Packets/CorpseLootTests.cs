using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Domain.GameData.Tables;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;
using RO2.Inventory.Domain;

namespace RO2.Tests.Packets;

/// <summary>
/// Corpse loot: the 2374 list builder, the corpse registry behind 1338/1322, the 1322 parse and bag planning,
/// and the party loot dice (1379/1380 -> 2468/2469/2470). Bodies are the RMI body after the opcode.
/// Shares the static corpse registry with CorpseShareRigCollection.
/// </summary>
[Collection(CorpseShareRigCollection.Name)]
public class CorpseLootTests
{
    private static byte[] Hex(string s) => Convert.FromHexString(s);
    private static string ToHex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    
    [Fact]
    public void List2374_TwoItems_IsTheReferenceFrame()
    {
        
        const string retail =
            "01545400aee200000000130260280002014dc4c901d7549712a83d040e010000000000000000000000000000000000" +
            "cf6e5401d8549712a83d040e01000000000000000000000000000000000001a87cb1a7b3b6e00d";
        var body = PickupNotifyPackets.BuildHeartBeatItem2374(0x28600213, new[]
        {
            new GroundItemService.CorpseItem(0x0E043DA8129754D7, 0x01C9C44D, 1),
            new GroundItemService.CorpseItem(0x0E043DA8129754D8, 0x01546ECF, 1),
        }, new ulong[] { 0x0DE0B6B3A7B17CA8 });
        Assert.Equal(retail, ToHex(body));
    }

    [Fact]
    public void List2374_FiveItems_TakesTheTwoByteLengthPrefix()
    {
        
        const string retail =
            "02ae00ae00aee200000000131460280005016bc4c90151559712a83d040e020000000000000000000000000000000000" +
            "d16e540152559712a83d040e0100000000000000000000000000000000004f86310253559712a83d040e0100000000000000" +
            "000000000000000000009d65c60054559712a83d040e010000000000000000000000000000000000758cf70155559712a83d" +
            "040e01000000000000000000000000000000000001a87cb1a7b3b6e00d";
        var body = PickupNotifyPackets.BuildHeartBeatItem2374(0x28601413, new[]
        {
            new GroundItemService.CorpseItem(0x0E043DA812975551, 0x01C9C46B, 2),
            new GroundItemService.CorpseItem(0x0E043DA812975552, 0x01546ED1, 1),
            new GroundItemService.CorpseItem(0x0E043DA812975553, 0x0231864F, 1),
            new GroundItemService.CorpseItem(0x0E043DA812975554, 0x00C6659D, 1),
            new GroundItemService.CorpseItem(0x0E043DA812975555, 0x01F78C75, 1),
        }, new ulong[] { 0x0DE0B6B3A7B17CA8 });
        Assert.Equal(retail, ToHex(body));
    }

    [Fact]
    public void List2374_RefusesAnEmptyList()
    {
        
        Assert.Throws<ArgumentException>(() => PickupNotifyPackets.BuildHeartBeatItem2374(0x13,
            Array.Empty<GroundItemService.CorpseItem>(), new ulong[] { 1 }));
    }

    
    
    
    
    private const string Retail1322 = "01151500a3e211520f074d668c0da83d040e0013e79d01";

    [Fact]
    public void Parse1322_ReadsTheLootUidAndTheCorpse()
    {
        Assert.True(ItemPickUpHandler.TryParse(Hex(Retail1322), out var uid, out var type, out var corpse));
        Assert.Equal(0x0E043DA80D8C664DUL, uid);
        Assert.Equal((byte)0, type);
        Assert.Equal(0x019DE713u, corpse);
    }

    [Fact]
    public void RetailExchange_ListAndRevoke_AreByteExact()
    {
        var list = PickupNotifyPackets.BuildHeartBeatItem2374(0x019DE713,
            new[] { new GroundItemService.CorpseItem(0x0E043DA80D8C664D, 0x02625EBB, 1) },
            new ulong[] { 0x0DE0B6B3A7E7D981 });
        Assert.Equal("01363600aee20000000013e79d01000101bb5e62024d668c0da83d040e0100000000000000000000000000000000000181d9e7a7b3b6e00d",
            ToHex(list));
        Assert.Equal("010f0f00dee300000000010013e79d0100", ToHex(CombatPackets.BuildLootingAuthority2471(0x019DE713, granted: false)));
    }

    [Fact]
    public void Notify2334_MatchesTheRetailAnswerThroughTheTemplate()
    {
        
        
        
        const string retailHead = "0282008200a4e20000000011520f074d668c0da83d040ebb5e6202";
        var body = PickupNotifyPackets.BuildPickupItemNotify2334(0x070F5211, 0x0E043DA80D8C664D, 0x02625EBB);
        Assert.Equal(133, body.Length);
        Assert.StartsWith(retailHead, ToHex(body));
        Assert.All(body.Skip(retailHead.Length / 2), b => Assert.Equal(0, b));
    }

    [Fact]
    public void FullBagRefusal_IsTheLoneRetail2333()
    {
        
        
        var body = LootNotifyPackets.BuildPickupItem2333(new byte[LootNotifyPackets.ItemRecordLength],
            resultCode: ItemPickUpHandler.NoFreeBagCell);
        Assert.Equal("344e0000" + new string('0', 2 * LootNotifyPackets.ItemRecordLength), ToHex(body));
    }

    
    private static readonly DateTime Died = new(2026, 9, 23, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Registry_ListsTakesAndEmpties_ForTheLooterOnly()
    {
        const uint map = 900001, corpse = 0x00AA0113, owner = 77;
        GroundItemService.BeginCorpse(map, corpse, 50000001, Died, owner, 0x0DE0B6B3A7000001, nowUtc: Died);
        var a = GroundItemService.AddCorpseItem(map, corpse, 30000205, 2)!;
        var b = GroundItemService.AddCorpseItem(map, corpse, 22310607, 1)!;
        var now = Died.AddSeconds(5);

        Assert.True(GroundItemService.TryGetCorpseLoot(map, corpse, owner, Died, out var loot, now));
        Assert.Equal(new[] { a.Uid, b.Uid }, loot.Items.Select(i => i.Uid).ToArray());
        Assert.Equal(GroundItemService.ItemUidHighBand, (uint)(a.Uid >> 32));
        Assert.False(GroundItemService.TryGetCorpseLoot(map, corpse, owner + 1, Died, out _, now)); 

        Assert.True(GroundItemService.TryTakeCorpseItem(map, corpse, a.Uid, owner, Died, out var took, out var left, now));
        Assert.Equal((30000205u, 2, 1), (took.ItemTemplateId, took.Count, left));
        Assert.False(GroundItemService.TryTakeCorpseItem(map, corpse, a.Uid, owner, Died, out _, out _, now)); 
        Assert.True(GroundItemService.HasCorpseLoot(map, corpse, Died, now));

        Assert.True(GroundItemService.TryTakeCorpseItem(map, corpse, b.Uid, owner, Died, out _, out left, now));
        Assert.Equal(0, left);
        Assert.False(GroundItemService.HasCorpseLoot(map, corpse, Died, now));
        Assert.False(GroundItemService.TryBuildCorpseList2374(map, corpse, owner, Died, out _)); 
    }

    [Fact]
    public void Registry_ForgetsACorpseWhoseHandleRespawned()
    {
        const uint map = 900002, corpse = 0x00AA0213, owner = 78;
        GroundItemService.BeginCorpse(map, corpse, 50000001, Died, owner, 1, nowUtc: Died);
        GroundItemService.AddCorpseItem(map, corpse, 30000205, 1);
        var now = Died.AddSeconds(5);

        Assert.False(GroundItemService.TryGetCorpseLoot(map, corpse, owner, null, out _, now));             
        GroundItemService.BeginCorpse(map, corpse, 50000001, Died, owner, 1, nowUtc: Died);
        GroundItemService.AddCorpseItem(map, corpse, 30000205, 1);
        Assert.False(GroundItemService.HasCorpseLoot(map, corpse, Died.AddSeconds(40), now));              
        Assert.False(GroundItemService.HasCorpseLoot(map, corpse, Died, now));                              
    }

    [Fact]
    public void Registry_LapsesAfterTheUnlootedLifetime()
    {
        const uint map = 900003, corpse = 0x00AA0313, owner = 79;
        GroundItemService.BeginCorpse(map, corpse, 50000001, Died, owner, 1, nowUtc: Died);
        GroundItemService.AddCorpseItem(map, corpse, 30000205, 1);
        Assert.True(GroundItemService.HasCorpseLoot(map, corpse, Died, Died.AddSeconds(119)));
        Assert.False(GroundItemService.HasCorpseLoot(map, corpse, Died, Died + GroundItemService.UnlootedCorpseLifetime));
    }

    [Fact]
    public void Registry_AddsNothingToACorpseThatWasNeverBegun()
    {
        Assert.Null(GroundItemService.AddCorpseItem(900004, 0x00AA0413, 30000205, 1));
    }

    
    
    
    
    
    
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Despawn_WithNoLootRegistered_IsThePlainCorpseDisappearDelay()
    {
        var wait = GroundItemService.WaitUntilCorpseMayLeaveAsync(900010, 0x00AA1013, DateTime.UtcNow,
            TimeSpan.FromMilliseconds(20));
        await wait.WaitAsync(Generous);
    }

    [Fact]
    public async Task Despawn_WaitsForTheLastPickup_ThenForgetsTheCorpse()
    {
        const uint map = 900011, corpse = 0x00AA1113, owner = 81;
        var died = DateTime.UtcNow;
        GroundItemService.BeginCorpse(map, corpse, 50000001, died, owner, 1);
        var a = GroundItemService.AddCorpseItem(map, corpse, 30000205, 1)!;
        var b = GroundItemService.AddCorpseItem(map, corpse, 22310607, 1)!;

        var wait = GroundItemService.WaitUntilCorpseMayLeaveAsync(map, corpse, died, TimeSpan.FromMilliseconds(20));
        await Task.Delay(150);
        Assert.False(wait.IsCompleted); 

        Assert.True(GroundItemService.TryTakeCorpseItem(map, corpse, a.Uid, owner, died, out _, out _));
        await Task.Delay(150);
        Assert.False(wait.IsCompleted); 

        Assert.True(GroundItemService.TryTakeCorpseItem(map, corpse, b.Uid, owner, died, out _, out var left));
        Assert.Equal(0, left);
        await wait.WaitAsync(Generous);
        Assert.False(GroundItemService.HasCorpseLoot(map, corpse, died));
    }

    [Fact]
    public async Task Despawn_KeepsWaiting_WhenTheLastItemIsPutBack()
    {
        const uint map = 900012, corpse = 0x00AA1213, owner = 82;
        var died = DateTime.UtcNow;
        GroundItemService.BeginCorpse(map, corpse, 50000001, died, owner, 1);
        var a = GroundItemService.AddCorpseItem(map, corpse, 30000205, 1)!;

        var wait = GroundItemService.WaitUntilCorpseMayLeaveAsync(map, corpse, died, TimeSpan.FromMilliseconds(100));
        Assert.True(GroundItemService.TryTakeCorpseItem(map, corpse, a.Uid, owner, died, out var took, out _));
        GroundItemService.ReturnCorpseItem(map, corpse, took); 
        Assert.True(GroundItemService.HasCorpseLoot(map, corpse, died));

        await Task.Delay(400); 
        Assert.False(wait.IsCompleted);

        GroundItemService.RemoveCorpse(map, corpse); 
        await wait.WaitAsync(Generous);
    }

    
    
    
    
    
    
    
    
    
    private static readonly SpawnEntry RespawnSpawn = new()
    {
        MapId = 1300, Index = 383, PosX = 447.525635f, PosY = 321.107849f, PosZ = 12.481079f, Rot = 300f,
        AssetIndex = 28580000,
    };

    private static (NpcStateManager Npcs, uint Handle) OneLiveNpc()
    {
        var npcs = new NpcStateManager(new GameDataStore(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<NpcStateManager>.Instance);
        npcs.TrackSpawns(RespawnSpawn.MapId, [RespawnSpawn]);
        return (npcs, npcs.AllocateHandle(RespawnSpawn.MapId, RespawnSpawn.Index));
    }

 /// <summary>The death stamp the 1338/1322/1379 handlers pass for a corpse handle.</summary>
    private static DateTime? CorpseStamp(NpcStateManager npcs, uint handle)
        => npcs.GetNpc(handle) is { IsDead: true } corpse ? corpse.DeathTime : null;

    [Fact]
    public async Task Respawn_WhileTheCorpseIsShown_TakesAFreshHandle_AndTheCorpseKeepsItsOwn()
    {
        const uint owner = 88;
        var map = RespawnSpawn.MapId;
        var (npcs, handle) = OneLiveNpc();
        Assert.True(npcs.ApplyDamage(handle, int.MaxValue).Died);
        var died = npcs.GetNpc(handle)!.DeathTime!.Value;

        
        
        GroundItemService.BeginCorpse(map, handle, RespawnSpawn.AssetIndex, died, owner, 1);
        GroundItemService.AddCorpseItem(map, handle, 30000205, 1);
        var pending = LootService.TrackPendingCorpse(handle, died);
        var wait = GroundItemService.WaitUntilCorpseMayLeaveAsync(map, handle, died, TimeSpan.FromMilliseconds(20));
        try
        {
            var fresh = npcs.Respawn(handle);

            
            Assert.NotEqual(0u, fresh);
            Assert.NotEqual(handle, fresh);
            Assert.Equal(handle & 0xFF, fresh & 0xFF);
            Assert.Equal(fresh, npcs.AllocateHandle(map, RespawnSpawn.Index));
            var live = npcs.GetNpc(fresh)!;
            Assert.Equal(fresh, live.EntityOid);
            Assert.False(live.IsDead);
            Assert.Null(live.DeathTime);
            Assert.Equal(live.MaxHp, live.Hp);

            
            
            var corpse = npcs.GetNpc(handle)!;
            Assert.NotSame(live, corpse);
            Assert.Equal(handle, corpse.EntityOid);
            Assert.True(corpse.IsDead);
            Assert.Equal(died, corpse.DeathTime);
            Assert.Equal((0, false), npcs.ApplyDamage(handle, 10));
            Assert.Equal(0u, npcs.Respawn(handle));

            
            Assert.True(GroundItemService.TryGetCorpseLoot(map, handle, owner, CorpseStamp(npcs, handle), out _));
            await Task.Delay(150);
            Assert.False(wait.IsCompleted);

            
            npcs.RetireCorpse(handle, died.AddSeconds(-40));
            Assert.Same(corpse, npcs.GetNpc(handle));

            
            GroundItemService.RemoveCorpse(map, handle);
            await wait.WaitAsync(Generous);
            LootService.ReleasePendingCorpse(handle, pending);
            npcs.RetireCorpse(handle, died);
            Assert.Null(npcs.GetNpc(handle));
            Assert.False(GroundItemService.TryGetCorpseLoot(map, handle, owner, CorpseStamp(npcs, handle), out _));
            Assert.Same(live, npcs.GetNpc(fresh));
        }
        finally
        {
            LootService.ReleasePendingCorpse(handle, pending);
            GroundItemService.RemoveCorpse(map, handle);
        }
    }

    [Fact]
    public void Respawn_AfterTheCorpseLeft_LeavesNothingUnderTheOldHandle()
    {
        var (npcs, handle) = OneLiveNpc();
        Assert.True(npcs.ApplyDamage(handle, int.MaxValue).Died);
        Assert.False(LootService.CorpseOnClients(handle, npcs.GetNpc(handle)!.DeathTime)); 

        var fresh = npcs.Respawn(handle);

        Assert.NotEqual(handle, fresh);
        Assert.Null(npcs.GetNpc(handle));
        Assert.False(npcs.GetNpc(fresh)!.IsDead);
        Assert.Equal(fresh, npcs.AllocateHandle(RespawnSpawn.MapId, RespawnSpawn.Index));
    }

    [Fact]
    public void CorpseOnClients_IsOnlyItsOwnDeath_UntilItsRelease()
    {
        const uint handle = 0x00AA4113;
        var first = LootService.TrackPendingCorpse(handle, Died);
        Assert.False(LootService.CorpseOnClients(handle, null));                 
        Assert.False(LootService.CorpseOnClients(handle, Died.AddSeconds(40))); 
        Assert.True(LootService.CorpseOnClients(handle, Died));
        Assert.True(LootService.CorpseOnClients(handle, Died));                 

        
        var second = LootService.TrackPendingCorpse(handle, Died.AddSeconds(40));
        LootService.ReleasePendingCorpse(handle, first);
        Assert.True(LootService.CorpseOnClients(handle, Died.AddSeconds(40)));
        LootService.ReleasePendingCorpse(handle, second);
        Assert.False(LootService.CorpseOnClients(handle, Died.AddSeconds(40)));
    }

    
    private static IReadOnlyDictionary<uint, ItemData> Items(params ItemData[] rows) => rows.ToDictionary(r => r.ID);

    [Fact]
    public void Plan_MergesWholeIntoAHeldStack()
    {
        var items = Items(new ItemData { ID = 30000205, Stack_Max = 99 });
        var held = new InventoryItem { Id = 5, CharacterId = 1, ItemId = 30000205, Count = 10, Slot = 3, StorageType = 0 };
        var plan = ItemPickUpHandler.PlanLanding(new[] { held }, 1, 30000205, 2, items);
        Assert.False(plan.Refused);
        Assert.Same(held, plan.MergeInto);
        Assert.Equal(12, plan.MergedCount);
        Assert.Empty(plan.NewStacks);
    }

    [Fact]
    public void Plan_OpensTheLowestFreeCell_WhenNoStackTakesItWhole()
    {
        var items = Items(new ItemData { ID = 30000205, Stack_Max = 99, Durability = 0 });
        var owned = new[]
        {
            new InventoryItem { Id = 5, CharacterId = 1, ItemId = 30000205, Count = 98, Slot = 0, StorageType = 0 },
            new InventoryItem { Id = 6, CharacterId = 1, ItemId = 11100001, Count = 1, Slot = 1, StorageType = 0 },
        };
        var plan = ItemPickUpHandler.PlanLanding(owned, 1, 30000205, 2, items);
        Assert.Null(plan.MergeInto);
        var stack = Assert.Single(plan.NewStacks);
        Assert.Equal((short)2, stack.Slot);
        Assert.Equal(2, stack.Count);
    }

    [Fact]
    public void Plan_RefusesWhenTheBagIsFull()
    {
        var items = Items(new ItemData { ID = 11100001, Stack_Max = 1 });
        var owned = Enumerable.Range(0, BagCapacity.MainBagCells)
            .Select(s => new InventoryItem { Id = (ulong)(100 + s), CharacterId = 1, ItemId = 11100001, Count = 1, Slot = (short)s, StorageType = 0 })
            .ToArray();
        Assert.True(ItemPickUpHandler.PlanLanding(owned, 1, 11100001, 1, items).Refused);
    }

    
    [Fact]
    public void Registry_KeepsTheDiceMark_AndAnAllPassReleasesIt()
    {
        const uint map = 900030, corpse = 0x00AA3013, owner = 93;
        GroundItemService.BeginCorpse(map, corpse, 50000001, Died, owner, 1, nowUtc: DateTime.UtcNow);
        var plain = GroundItemService.AddCorpseItem(map, corpse, 30000196, 1)!;
        var rolled = GroundItemService.AddCorpseItem(map, corpse, 22300102, 1, diceBound: true)!;
        Assert.False(plain.DiceBound);
        Assert.True(rolled.DiceBound);

        Assert.True(GroundItemService.ReleaseDiceBound(map, corpse, rolled.Uid));
        Assert.True(GroundItemService.TryGetCorpseLoot(map, corpse, owner, Died, out var loot));
        Assert.All(loot.Items, i => Assert.False(i.DiceBound));
        Assert.False(GroundItemService.ReleaseDiceBound(map, corpse, 0x0E043DA8FFFFFFFF)); 
    }

    [Fact]
    public void Registry_TakesTheLifetimeTheKillGives()
    {
        const uint map = 900031, corpse = 0x00AA3113, owner = 94;
        GroundItemService.BeginCorpse(map, corpse, 50000001, Died, owner, 1, nowUtc: Died, unlootedLifetime: TimeSpan.FromSeconds(180));
        GroundItemService.AddCorpseItem(map, corpse, 30000205, 1);
        Assert.True(GroundItemService.HasCorpseLoot(map, corpse, Died, Died.AddSeconds(179)));
        Assert.False(GroundItemService.HasCorpseLoot(map, corpse, Died, Died.AddSeconds(180)));
    }

    [Fact]
    public void Registry_DoesNotLapseWhileARollHoldsIt()
    {
        const uint map = 900033, corpse = 0x00AA3313, owner = 96;
        GroundItemService.BeginCorpse(map, corpse, 50000001, Died, owner, 1, nowUtc: Died);
        GroundItemService.AddCorpseItem(map, corpse, 22300102, 1, diceBound: true);
        Assert.True(GroundItemService.HoldCorpse(map, corpse));
        var late = Died + GroundItemService.UnlootedCorpseLifetime + TimeSpan.FromSeconds(60);
        Assert.True(GroundItemService.HasCorpseLoot(map, corpse, Died, late));   
        GroundItemService.ReleaseCorpse(map, corpse);
        Assert.False(GroundItemService.HasCorpseLoot(map, corpse, Died, late));  
    }

 /// <summary>
 /// A corpse whose items are never taken lets its looters stay after its unlooted lifetime ends, not at the
 /// lapse. Scaled down for tests: a 100 ms lifetime and a 200 ms stay end no earlier than 300 ms after death.
 /// </summary>
    [Fact]
    public async Task Despawn_OfAnUnlootedCorpse_WaitsItsStayAfterTheLifetime()
    {
        const uint map = 900071, corpse = 0x00AA7113, owner = 101;
        var died = DateTime.UtcNow;
        GroundItemService.BeginCorpse(map, corpse, 50000001, died, owner, 1, nowUtc: died,
            unlootedLifetime: TimeSpan.FromMilliseconds(100));
        GroundItemService.AddCorpseItem(map, corpse, 30000205, 1);

        var wait = GroundItemService.WaitUntilCorpseMayLeaveAsync(map, corpse, died, TimeSpan.FromMilliseconds(200));
        await Task.Delay(180);
        Assert.False(wait.IsCompleted);                                            
        Assert.False(GroundItemService.HasCorpseLoot(map, corpse, died, DateTime.UtcNow)); 
        await wait.WaitAsync(Generous);
        
        Assert.True(DateTime.UtcNow - died >= TimeSpan.FromMilliseconds(280));
    }

 /// <summary>
 /// The unlooted lifetime by NPCInfo.Grade, from the shipped Config_Def: grades 12 and 13 use type 23 (180 s);
 /// every other grade uses type 22 (120 s). Grades 14 and 15 keep type 22.
 /// </summary>
    [Theory]
    [InlineData(13, 180)]
    [InlineData(12, 180)]
    [InlineData(11, 120)]
    [InlineData(14, 120)]
    public async Task UnlootedLifetime_FollowsTheGrade_FromTheShippedConfigDef(int grade, int seconds)
    {
        var config = await ShippedConfigDefAsync();
        var npc = new NpcData { ID = 50000001, Grade = grade };
        Assert.Equal(TimeSpan.FromSeconds(seconds),
            LootService.UnlootedCorpseLifetime(config, npc, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance));
    }

    [Fact]
    public void UnlootedLifetime_FallsBackToType22_ThenTo120s()
    {
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        var elite = new NpcData { ID = 1, Grade = 13 };
        var only22 = new List<ConfigDef> { new() { type = 22, Value = "150" } };
        Assert.Equal(TimeSpan.FromSeconds(150), LootService.UnlootedCorpseLifetime(only22, elite, logger));
        var unreadable = new List<ConfigDef> { new() { type = 23, Value = "x" }, new() { type = 22, Value = "0" } };
        Assert.Equal(GroundItemService.UnlootedCorpseLifetime, LootService.UnlootedCorpseLifetime(unreadable, elite, logger));
        Assert.Equal(GroundItemService.UnlootedCorpseLifetime,
            LootService.UnlootedCorpseLifetime(Array.Empty<ConfigDef>(), null, logger));
    }

    private static async Task<List<ConfigDef>> ShippedConfigDefAsync()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "data", "Config_Def.json")))
            dir = dir.Parent;
        if (dir == null)
            throw new DirectoryNotFoundException("no ancestor of " + AppContext.BaseDirectory + " holds data/Config_Def.json");
        await using var fs = File.OpenRead(Path.Combine(dir.FullName, "data", "Config_Def.json"));
        return (await System.Text.Json.JsonSerializer.DeserializeAsync<List<ConfigDef>>(fs))!;
    }

    [Fact]
    public async Task Despawn_WaitsForAnOpenRoll_PastTheUnlootedLifetime()
    {
        const uint map = 900013, corpse = 0x00AA1313, owner = 83;
        var died = DateTime.UtcNow;
        GroundItemService.BeginCorpse(map, corpse, 50000001, died, owner, 1, unlootedLifetime: TimeSpan.FromMilliseconds(100));
        GroundItemService.AddCorpseItem(map, corpse, 22300102, 1, diceBound: true);
        Assert.True(GroundItemService.HoldCorpse(map, corpse));

        var wait = GroundItemService.WaitUntilCorpseMayLeaveAsync(map, corpse, died, TimeSpan.FromMilliseconds(20));
        await Task.Delay(400);
        Assert.False(wait.IsCompleted); 

        GroundItemService.ReleaseCorpse(map, corpse);
        await wait.WaitAsync(Generous);
    }

    [Fact]
    public void LooterCanStillTake_CountsWhatIsLeftForTheLooter()
    {
        const uint map = 900032, corpse = 0x00AA3213, owner = 95;
        GroundItemService.BeginCorpse(map, corpse, 50000001, Died, owner, 1, nowUtc: DateTime.UtcNow);
        GroundItemService.AddCorpseItem(map, corpse, 30000196, 1);
        GroundItemService.AddCorpseItem(map, corpse, 22300102, 1, diceBound: true); 
        Assert.Equal(2, ItemPickUpHandler.ItemsLooterCanStillTake(map, corpse, owner, Died));
        Assert.Equal(0, ItemPickUpHandler.ItemsLooterCanStillTake(map, corpse, owner + 1, Died)); 
    }

    
    [Theory]
    
    
    
    [InlineData(2, 2, 0, false)]
    [InlineData(2, 2, 2, true)]
    [InlineData(2, 2, 3, true)]
    [InlineData(2, 2, 4, true)]
    [InlineData(2, 4, 3, false)]
    [InlineData(2, 4, 4, true)]
    [InlineData(3, 0, 4, false)]
    [InlineData(7, 4, 4, false)]
    [InlineData(2, 0, 5, false)] 
    public void LootRule_RollsWhatReachesTheQuality_InModeTwoOnly(int mode, int quality, int grade, bool rolls)
        => Assert.Equal(rolls, PartyDiceRolls.SendsToDice((byte)mode, (byte)quality, (byte)grade));

    [Fact]
    public void LootRule_NeedsAPartyAndAnItemRow()
    {
        var party = new PartyState { ItemRule = 2, ItemQuality = 2 };
        Assert.True(PartyDiceRolls.SendsToDice(party, new ItemData { ID = 22300102, Grade = 3 }));
        Assert.False(PartyDiceRolls.SendsToDice(null, new ItemData { ID = 22300102, Grade = 3 }));
        Assert.False(PartyDiceRolls.SendsToDice(party, null));
    }

    [Fact]
    public void Rolls_StayInTheBandTheChoiceSelects()
    {
        var rng = new Random(1);
        for (int i = 0; i < 5000; i++)
        {
            Assert.True(PartyDiceRolls.TryRoll(PartyDiceRolls.ChoiceHigh, rng, out var high));
            Assert.InRange(high, (ushort)1001, (ushort)2000);
            Assert.True(PartyDiceRolls.TryRoll(PartyDiceRolls.ChoiceLow, rng, out var low));
            Assert.InRange(low, (ushort)1, (ushort)1000);
        }
        Assert.False(PartyDiceRolls.TryRoll(PartyDiceRolls.ChoicePass, rng, out _));
        Assert.False(PartyDiceRolls.TryRoll(7, rng, out _));
    }

    [Fact]
    public void Winner_IsTheHighestRoll_FirstAnswerOnATie()
    {
        
        var rolls = new (ushort, long)[] { (1793, 0), (1837, 1), (1565, 2), (1830, 3), (1243, 4), (1672, 5), (524, 6) };
        Assert.Equal(1, PartyDiceRolls.PickWinner(rolls));
        Assert.Equal(0, PartyDiceRolls.PickWinner(new (ushort, long)[] { (1500, 0), (1500, 1) }));
        Assert.Equal(-1, PartyDiceRolls.PickWinner(Array.Empty<(ushort, long)>()));
    }

    
    [Fact]
    public void Parse1379_ReadsTheItemAndTheCorpse()
    {
        
        Assert.True(PartyDicePackets.TryParseDiceStart1379(Hex("011414008fe31140170e3981080ea83d040e13f0f940"),
            out var uid, out var corpse));
        Assert.Equal(0x0E043DA80E088139UL, uid);
        Assert.Equal(0x40F9F013u, corpse);
        Assert.False(PartyDicePackets.TryParseDiceStart1379(Hex(Retail1322), out _, out _)); 
    }

    [Theory]
    [InlineData("0113130091e311e81b070000002f558f0da83d040e", 0, 0x0E043DA80D8F552FUL)] 
    [InlineData("0113130091e3117259080100027652b00da83d040e", 2, 0x0E043DA80DB05276UL)] 
    public void Parse1380_ReadsTheChoiceAndTheItem(string body, int choice, ulong uid)
    {
        Assert.True(PartyDicePackets.TryParseDiceJoin1380(Hex(body), out var c, out var u));
        Assert.Equal(((byte)choice, uid), (c, u));
    }

    [Theory]
    [InlineData(0x0E043DA80D8F552FUL, 22300102u, 1, "0115150090e3000000002f558f0da83d040ec645540101")] 
    [InlineData(0x0E043DA80D8F5530UL, 22300201u, 9, "0115150090e30000000030558f0da83d040e2946540109")] 
    public void Open2468_IsTheRetailFrame(ulong uid, uint template, int count, string retail)
        => Assert.Equal(retail, ToHex(PartyDicePackets.BuildRouletteOpen2468(uid, template, count)));

    [Fact]
    public void Result2469_IsTheRetailFrame_ForARollAndForTheAllPass()
    {
        
        Assert.Equal("0120200092e3000000000000860681d9e7a7b3b6e00dc64554012f558f0da83d040e",
            ToHex(PartyDicePackets.BuildRouletteResult2469(0, 1670, 0x0DE0B6B3A7E7D981, 22300102, 0x0E043DA80D8F552F)));
        
        Assert.Equal("0120200092e300000000733d000000000000000000002ddbfe00d9f24f0ea83d040e",
            ToHex(PartyDicePackets.BuildRouletteResult2469(PartyDicePackets.ResultAllPassed, 0, 0, 16702253, 0x0E043DA80E4FF2D9)));
    }

    [Fact]
    public void Refuse2470_IsTheRetailFrame()
    {
        
        Assert.Equal("011c1c0093e3000000004cab56ab8f3c296fd06e54010d278a0da83d040e",
            ToHex(PartyDicePackets.BuildRouletteRefuse2470(0x6F293C8FAB56AB4C, 22310608, 0x0E043DA80D8A270D)));
    }
}

/// <summary>
/// Dungeon finder: 1555 -> 2804/2806/2808, 1559 -> 2824 and the boss kill -> 2816. Find_Dungeon_List and
/// Find_Dungeon_Reward rows are copied from data/*.json; the tests build no GameDataStore, whose load would
/// swap the process-wide GameDataStore.Loaded under tests running in parallel.
/// </summary>
public class DungeonFinderTests
{
    private static byte[] Hex(string s) => Convert.FromHexString(s);
    private static string ToHex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    
    private static readonly FindDungeonList List2 = new() { List_ID = 2, Category_ID = 3, Dungeon_ID = 6, Party_Num = 5, Role_Tanker = 1, Role_Healer = 1, Role_Dealer = 3, Min_Level = 50, Max_Level = 50, Last_Boss_ID = 70090000 };
    private static readonly FindDungeonList List4 = new() { List_ID = 4, Category_ID = 3, Dungeon_ID = 8, Party_Num = 5, Role_Tanker = 1, Role_Healer = 1, Role_Dealer = 3, Min_Level = 50, Max_Level = 50, Last_Boss_ID = 70070000 };
    private static readonly FindDungeonList List8 = new() { List_ID = 8, Category_ID = 3, Dungeon_ID = 12, Party_Num = 5, Role_Tanker = 1, Role_Healer = 1, Role_Dealer = 3, Min_Level = 50, Max_Level = 50, Last_Boss_ID = 70780000 };
    private static readonly FindDungeonList List21 = new() { List_ID = 21, Category_ID = 5, Dungeon_ID = 63, Party_Num = 1, Role_Tanker = 0, Role_Healer = 0, Role_Dealer = 1, Min_Level = 10, Max_Level = 50, Last_Boss_ID = 75000008, Reward_GroupID = 3 };

    
    private static readonly FindDungeonCategory[] Categories =
    [
        new() { Category_ID = 2, Reward_GroupID = 1 },
        new() { Category_ID = 3, Reward_GroupID = 2 },
    ];
    private static readonly FindDungeonReward[] Rewards =
    [
        new() { ID = 2, Reward_GroupID = 2, MinLevel = 1, MaxLevel = 255, _1st_Item_ID = 51000006, _1st_Item_Num = 1 },
        new() { ID = 3, Reward_GroupID = 3, MinLevel = 10, MaxLevel = 25, Money = 1000, Exp = 2500, _1st_Item_ID = 22402017, _1st_Item_Num = 1 },
        new() { ID = 4, Reward_GroupID = 3, MinLevel = 26, MaxLevel = 50, Money = 2600, Exp = 5000, _1st_Item_ID = 22402017, _1st_Item_Num = 2, _2nd_Item_ID = 22402022, _2nd_Item_Num = 1 },
        new() { ID = 5, Reward_GroupID = 3, MinLevel = 51, MaxLevel = 255, Money = 5100, Exp = 10000, _1st_Item_ID = 22402017, _1st_Item_Num = 5, _2nd_Item_ID = 22402022, _2nd_Item_Num = 1, _3rd_Item_ID = 22402097, _3rd_Item_Num = 1 },
    ];

    private static PlayerState Pc(uint session) => new() { SessionId = session, EntityId = (session << 8) | 0x11, Level = 50 };

    private static Func<uint, FindDungeonList?> Rows(params FindDungeonList[] rows)
        => id => rows.FirstOrDefault(r => r.List_ID == id);

    [Theory]
    [InlineData("0700000002010015000000", 2, 21u)]   
    [InlineData("0700000002010004000000", 2, 4u)]    
    [InlineData("0700000001010001000000", 1, 1u)]    
    public void Parse1555_ReadsTheRetailFrames(string body, int role, uint list)
    {
        Assert.True(DungeonFinderPackets.TryParseFindDungeon1555(Hex(body), out var r, out var lists));
        Assert.Equal((byte)role, r);
        Assert.Equal(new[] { list }, lists);
    }

    [Theory]
    [InlineData("07000000020100150000")]              
    [InlineData("0b000000020200150000000400000000")]  
    [InlineData("03000000020000")]                    
    public void Parse1555_RefusesOtherShapes(string body)
        => Assert.False(DungeonFinderPackets.TryParseFindDungeon1555(Hex(body), out _, out _));

    [Fact]
    public void Answer2804_IsTheRetailFrame_ForAcceptAndForLevel()
    {
        Assert.Equal("0000", ToHex(DungeonFinderPackets.BuildFindDungeonAnswer2804(DungeonFinderPackets.ResultAccepted)));
        
        Assert.Equal("1100", ToHex(DungeonFinderPackets.BuildFindDungeonAnswer2804(DungeonFinderPackets.ResultLevel)));
    }

    [Fact]
    public void SoloList21_MatchesAtOnce_WithTheRetailBurst()
    {
        var queue = new DungeonFinderQueue();
        var pc = Pc(1);
        var outcome = queue.Join(pc, DungeonFinderPackets.RoleDealer, [List21]);

        Assert.False(outcome.AlreadyQueued);
        Assert.NotNull(outcome.Match);
        
        Assert.Equal("00001500000000010002", ToHex(DungeonFinderPackets.BuildInProgressInfo2806(outcome.ShownListId, outcome.Shown, outcome.OwnRole)));
        Assert.Equal("150000000200", ToHex(DungeonFinderPackets.BuildStartJoinMessage2808(outcome.Match.ListId, outcome.Match.Role)));
        Assert.Equal("15000000000100", ToHex(DungeonFinderPackets.BuildInProgressJoinMsg2824(outcome.Match.ListId, outcome.Match.Group)));

        
        Assert.Empty(queue.MembersOf(21));
        Assert.NotNull(queue.Join(pc, DungeonFinderPackets.RoleDealer, [List21]).Match);
    }

    [Fact]
    public void SoloList21_WithNoDealerFlag_HasNoSlot_AndStaysQueued()
    {
        var queue = new DungeonFinderQueue();
        var outcome = queue.Join(Pc(1), DungeonFinderPackets.RoleTank, [List21]);
        Assert.Null(outcome.Match);
        Assert.Single(queue.MembersOf(21));
        Assert.Equal("00001500000000000000", ToHex(DungeonFinderPackets.BuildInProgressInfo2806(outcome.ShownListId, outcome.Shown, outcome.OwnRole)));
    }

    [Fact]
    public void PartyList4_Queues_WithoutAMatch_AndRefusesASecondSearch()
    {
        var queue = new DungeonFinderQueue();
        var pc = Pc(1);
        var outcome = queue.Join(pc, DungeonFinderPackets.RoleDealer, [List4]);
        Assert.Null(outcome.Match);
        
        Assert.Equal("00000400000000010002", ToHex(DungeonFinderPackets.BuildInProgressInfo2806(outcome.ShownListId, outcome.Shown, outcome.OwnRole)));
        Assert.True(queue.Join(pc, DungeonFinderPackets.RoleDealer, [List4]).AlreadyQueued);

        Assert.Same(pc, queue.Leave(pc)?.Player);
        Assert.Null(queue.Leave(pc));
    }

    [Fact]
    public async Task List2_SecondDealer_PushesTheRetailUpdates_OnJoinAndLeave()
    {
        var queue = new DungeonFinderQueue();
        var first = Pc(1);
        var second = Pc(2);
        queue.Join(first, DungeonFinderPackets.RoleDealer, [List2]);
        var joined = queue.Join(second, DungeonFinderPackets.RoleDealer, [List2]);
        
        Assert.Equal("00000200000000020002", ToHex(DungeonFinderPackets.BuildInProgressInfo2806(joined.ShownListId, joined.Shown, joined.OwnRole)));

        var sent = new List<(uint Session, string Body)>();
        Task Capture(uint s, byte[] b) { sent.Add((s, ToHex(b))); return Task.CompletedTask; }

        await queue.PushStateAsync(joined.Entry!.ListIds, second, Rows(List2), Capture);
        
        Assert.Equal([(1u, "00000200000000020002")], sent);

        sent.Clear();
        var left = queue.Leave(second);
        await queue.PushStateAsync(left!.ListIds, second, Rows(List2), Capture);
        
        Assert.Equal([(1u, "00000200000000010002")], sent);
    }

    [Fact]
    public void List8_HealerNextToADealer_IsTheCensusFrame()
    {
        
        var queue = new DungeonFinderQueue();
        queue.Join(Pc(1), DungeonFinderPackets.RoleDealer, [List8]);
        var healer = queue.Join(Pc(2), DungeonFinderPackets.RoleHealer, [List8]);
        Assert.Equal("00000800000001010004", ToHex(DungeonFinderPackets.BuildInProgressInfo2806(healer.ShownListId, healer.Shown, healer.OwnRole)));
    }

    [Fact]
    public async Task Prune_DropsGoneSessions_AndTellsTheRest()
    {
        var queue = new DungeonFinderQueue();
        var stays = Pc(1);
        var gone = Pc(2);
        queue.Join(stays, DungeonFinderPackets.RoleDealer, [List2]);
        queue.Join(gone, DungeonFinderPackets.RoleDealer, [List2]);

        var removed = queue.Prune(e => ReferenceEquals(e.Player, gone));
        Assert.Single(removed);
        var sent = new List<string>();
        await queue.PushStateAsync(removed.SelectMany(e => e.ListIds), null, Rows(List2), (s, b) => { sent.Add(ToHex(b)); return Task.CompletedTask; });
        Assert.Equal(["00000200000000010002"], sent);
    }

    [Fact]
    public void Gift2816_IsTheRetailFrame()
        => Assert.Equal("1500000002", ToHex(DungeonFinderPackets.BuildCompleteGift2816(21)));   

    [Theory]
    [InlineData(90, 5)]   
    [InlineData(50, 4)]
    [InlineData(10, 3)]
    public void RewardRow_IsChosenByTheRewardLevel(int level, int rowId)
        => Assert.Equal((byte)rowId, DungeonFinderReward.RewardRow(Categories, Rewards, List21, level)?.ID);

    [Fact]
    public void RewardRow_FallsBackToTheCategoryGroup_AndIsNullBelowTheFirstRow()
    {
        Assert.Equal((byte)2, DungeonFinderReward.RewardRow(Categories, Rewards, List4, 50)?.ID);
        Assert.Null(DungeonFinderReward.RewardRow(Categories, Rewards, List21, 9));
    }

    [Fact]
    public void RewardLevel_IsTheLevel_WhenNoProfessionIsModelled()
        => Assert.Equal(50, DungeonFinderReward.RewardLevel(Pc(1)));

    private static readonly Dictionary<uint, ItemData> Templates = new()
    {
        [22402017] = new ItemData { ID = 22402017, Stack_Max = 255 },
        [22402022] = new ItemData { ID = 22402022, Stack_Max = 255 },
        [22402097] = new ItemData { ID = 22402097, Stack_Max = 99 },
    };

    [Fact]
    public void PlanBag_AddsToTheHeldStacks_LikeTheRetailRewards()
    {
        
        var owned = new List<InventoryItem>
        {
            new() { Id = 1, CharacterId = 7, ItemId = 22402017, Count = 5, Slot = 0 },
            new() { Id = 2, CharacterId = 7, ItemId = 22402022, Count = 1, Slot = 1 },
            new() { Id = 3, CharacterId = 7, ItemId = 22402097, Count = 2, Slot = 2 },
        };
        var items = DungeonFinderReward.RewardItems(Rewards[3], Templates, "pc", Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        var plan = DungeonFinderReward.PlanBag(items, owned, 7, Templates);

        Assert.NotNull(plan);
        Assert.Equal([10, 2, 3], plan.Select(l => l.MergedCount));
        Assert.All(plan, l => Assert.Empty(l.NewStacks));
    }

    [Fact]
    public void PlanBag_RefusesARewardThatDoesNotFitWhole()
    {
        
        var owned = Enumerable.Range(0, 20)
            .Select(i => new InventoryItem { Id = (ulong)i + 1, CharacterId = 7, ItemId = 60000000u + (uint)i, Count = 1, Slot = (short)i })
            .ToList();
        var items = DungeonFinderReward.RewardItems(Rewards[3], Templates, "pc", Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        Assert.Null(DungeonFinderReward.PlanBag(items, owned, 7, Templates));

        
        owned.RemoveAt(19);
        Assert.Null(DungeonFinderReward.PlanBag(items, owned, 7, Templates));
    }

    [Fact]
    public void RewardPickup2333_CarriesTheFinderPickupKind()
    {
        
        var body = LootNotifyPackets.BuildPickupItem2333(new byte[LootNotifyPackets.ItemRecordLength],
            pickupKind: DungeonFinderReward.PickupKindFinderReward);
        Assert.Equal("00000300", ToHex(body[..4]));
    }
}
