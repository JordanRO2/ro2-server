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
/// Tests the death wear: the per-hit armour durability step and the death burst of worn-slot durability loss.
/// </summary>
public class DeathWearReauditTests
{
    
 /// <summary>Durability lost per hit by an armour piece: 1 for the Crescentia and Soul Maker job bits,
 /// 2 for every other armour.</summary>
    [Fact]
    public void ArmourWearStep_FollowsTheJobMask()
    {
        foreach (var mask in new long[] { 65536, 131072, 196608, 229376 })
            Assert.Equal(1, DurabilityService.ArmourWearStep(new ItemData { Require_Job = mask }));
        foreach (var mask in new long[] { 0, 1, 32767, 1 << 15, 1 << 18 })
            Assert.Equal(2, DurabilityService.ArmourWearStep(new ItemData { Require_Job = mask }));
    }

 /// <summary>
 /// The death burst: worn slots 1 to 12 in ascending order, each reduced by 950 and floored at 0, sent
 /// unicast to the victim. On a PK map the values are unchanged.
 /// </summary>
    [Fact]
    public async Task DeathWear_IsTheWornSlots1To12_950Off_AndNothingOffOnAPkMap()
    {
        var store = await LoadStoreAsync("Map_List.json");
        Assert.Equal(950, DurabilityService.DeathWearLossOn(store, 1100));
        Assert.Equal(0, DurabilityService.DeathWearLossOn(store, 4109));

        await using var net = await Harness.StartAsync(1);
        var players = new PlayerStateManager(new NoDirtySink());
        var broadcast = new BroadcastService(players, NullLogger<BroadcastService>.Instance);
        broadcast.SetServer(net.Server);
        const uint charId = 77;
        var bag = new GearInventory(
        [
            new InventoryItem { Id = 0x0E27C49888FD2126, CharacterId = charId, StorageType = 1, Slot = 1, Durability = 9950 },
            new InventoryItem { Id = 11, CharacterId = charId, StorageType = 1, Slot = 13, Durability = 5000 },  
            new InventoryItem { Id = 12, CharacterId = charId, StorageType = 1, Slot = 12, Durability = 0 },     
            new InventoryItem { Id = 13, CharacterId = charId, StorageType = 1, Slot = 6, Durability = 66 },
            new InventoryItem { Id = 14, CharacterId = charId, StorageType = 1, Slot = 20, Durability = 10000 }, 
            new InventoryItem { Id = 15, CharacterId = charId, StorageType = 0, Slot = 3, Durability = 10000 },  
        ]);
        var collection = new ServiceCollection();
        collection.AddScoped<IInventoryRepository>(_ => bag);
        await using var services = collection.BuildServiceProvider();
        var durability = new DurabilityService(store, NullLogger<DurabilityService>.Instance,
            services.GetRequiredService<IServiceScopeFactory>(), broadcast);
        var victim = new PlayerState { SessionId = net.Sessions[0].SessionId, CharacterId = charId, MapId = 1100 };

        await durability.ApplyDeathWearAsync(victim);
        var r = net.Readers[0];
        Assert.Equal((ZoneS2COpcodes.OnUpdateItemDurability, "01121200cde3000000002621fd8898c4270e2823"), Hexed(await r.NextAsync()));
        Assert.Equal((ZoneS2COpcodes.OnUpdateItemDurability, Hex(ItemPackets.BuildItemDurability2503(13, 0))), Hexed(await r.NextAsync()));
        Assert.Equal((ZoneS2COpcodes.OnUpdateItemDurability, Hex(ItemPackets.BuildItemDurability2503(12, 0))), Hexed(await r.NextAsync()));
        Assert.Equal((short)9000, bag.Items[0].Durability);   
        Assert.Equal((short)0, bag.Items[3].Durability);
        Assert.Equal((short)5000, bag.Items[1].Durability);   

        victim.MapId = 4109;   
        await durability.ApplyDeathWearAsync(victim);
        Assert.Equal((ZoneS2COpcodes.OnUpdateItemDurability, "01121200cde3000000002621fd8898c4270e2823"), Hexed(await r.NextAsync()));
        Assert.Equal((ZoneS2COpcodes.OnUpdateItemDurability, Hex(ItemPackets.BuildItemDurability2503(13, 0))), Hexed(await r.NextAsync()));
        Assert.Equal((ZoneS2COpcodes.OnUpdateItemDurability, Hex(ItemPackets.BuildItemDurability2503(12, 0))), Hexed(await r.NextAsync()));
        Assert.Equal((short)9000, bag.Items[0].Durability);
    }
}
