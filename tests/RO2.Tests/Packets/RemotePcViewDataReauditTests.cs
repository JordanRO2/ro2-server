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
using RO2.Characters.Domain;

namespace RO2.Tests.Packets;

/// <summary>Tests the remote PC view record (2315): the spawn exchange and the equipment change.</summary>
public class RemotePcViewDataReauditTests
{
    
 /// <summary>Each PC's 2315 is sent right after its 2504, in both directions, followed by the 2497. For one handle the order is 2504, 2315, 2497, 2492. The record matches the 2504, with the same worn gear. The 2591 checks are skipped without a database.</summary>
    [Fact]
    public async Task SpawnExchange_Sends2315AfterEach2504_BothWays()
    {
        await using var net = await Harness.StartAsync(2);
        var players = new PlayerStateManager(new NoDirtySink());
        var broadcast = new BroadcastService(players, NullLogger<BroadcastService>.Instance);
        broadcast.SetServer(net.Server);
        var existing = new PlayerState { SessionId = net.Sessions[0].SessionId, EntityId = 0x06EF5A11, CharacterId = 501,
            Name = "Standing", Level = 40, MapId = 1100, X = 10, Y = 20, Z = 3, Hp = 900, MaxHp = 900,
            Character = new Character { Id = 501, Name = "Standing", Job = 1, Level = 40 } };
        var newcomer = new PlayerState { SessionId = net.Sessions[1].SessionId, EntityId = 0x06EF5B11, CharacterId = 502,
            Name = "Arriving", Level = 30, MapId = 1100, X = 11, Y = 21, Z = 3, Hp = 700, MaxHp = 700,
            Character = new Character { Id = 502, Name = "Arriving", Job = 2, Level = 30 } };
        players.AddPlayer(existing.SessionId, existing);
        players.AddPlayer(newcomer.SessionId, newcomer);
        var gear = new InventoryItem { Id = 9001, CharacterId = 501, ItemId = 10100001, StorageType = 1, Slot = 3, Count = 1 };
        var collection = new ServiceCollection();
        collection.AddSingleton<IInventoryRepository>(new GearInventory([gear]));
        collection.AddSingleton(new GameDataStore());
        await using var services = collection.BuildServiceProvider();
        var spawn = new PlayerSpawnService(players, broadcast, NullLogger<PlayerSpawnService>.Instance,
            services.GetRequiredService<IServiceScopeFactory>(), TestEmblems.Create());

        await spawn.NotifyPlayerSpawnAsync(newcomer);

        
        await AssertShownAsync(net.Readers[0], newcomer, []);
        await AssertShownAsync(net.Readers[1], existing, [gear]);

        static async Task AssertShownAsync(FrameReader reader, PlayerState subject, InventoryItem[] worn)
        {
            var (op1, pcIn) = await reader.NextAsync();
            var (op2, viewData) = await reader.NextAsync();
            var (op3, _) = await reader.NextAsync();
            Assert.Equal(new[] { ZoneS2COpcodes.OnPC_In, ZoneS2COpcodes.OnPCViewData, ZoneS2COpcodes.OnChangeActorMoveSpeed },
                new[] { op1, op2, op3 });
            Assert.Equal(Hex(ActorStatePackets.BuildOnPCViewData2315(subject, subject.Character!, worn, TestEmblems.Create())), Hex(viewData));
            
            var inBlob = pcIn.AsSpan(1 + pcIn[0]).ToArray();
            var viewBlob = viewData.AsSpan(1 + viewData[0]).ToArray();
            Assert.Equal(subject.EntityId, BinaryPrimitives.ReadUInt32LittleEndian(viewBlob.AsSpan(8)));
            Assert.Equal(Hex(inBlob[10..]), Hex(viewBlob[12..]));
        }
    }

 /// <summary>An equipment-view change is sent to nearby players as the 2848 snapshot, followed by the PC's 2315 built from the same worn set. The PC itself receives neither.</summary>
    [Fact]
    public async Task EquipChange_Broadcasts2848_ThenThePcs2315_ToTheOthers()
    {
        await using var net = await Harness.StartAsync(2);
        var players = new PlayerStateManager(new NoDirtySink());
        var broadcast = new BroadcastService(players, NullLogger<BroadcastService>.Instance);
        broadcast.SetServer(net.Server);
        var pc = new PlayerState { SessionId = net.Sessions[0].SessionId, EntityId = 0x06EF5A11, CharacterId = 501,
            Name = "Dresser", Level = 40, MapId = 1100, Character = new Character { Id = 501, Name = "Dresser", Job = 1, Level = 40 } };
        var viewer = new PlayerState { SessionId = net.Sessions[1].SessionId, EntityId = 0x06EF5B11, MapId = 1100 };
        players.AddPlayer(pc.SessionId, pc);
        players.AddPlayer(viewer.SessionId, viewer);
        InventoryItem[] worn = [new InventoryItem { Id = 9001, CharacterId = 501, ItemId = 10100001, StorageType = 1, Slot = 3, Count = 1 }];

        await EquipViewNotifyPackets.BroadcastEquipChangeAsync(broadcast, pc, worn, TestEmblems.Create());
        await net.Sessions[0].SendInternalRmiAsync(ZoneS2COpcodes.OnLevelUp, new byte[] { 0x5A });

        Assert.Equal((ZoneS2COpcodes.NfyPCEquipView,
            Hex(EquipViewNotifyPackets.BuildEquipView2848(pc.EntityId, EquipViewNotifyPackets.BuildEquipRecords28(worn)))),
            Hexed(await net.Readers[1].NextAsync()));
        Assert.Equal((ZoneS2COpcodes.OnPCViewData, Hex(ActorStatePackets.BuildOnPCViewData2315(pc, pc.Character!, worn, TestEmblems.Create()))),
            Hexed(await net.Readers[1].NextAsync()));
        Assert.Equal((ZoneS2COpcodes.OnLevelUp, "5a"), Hexed(await net.Readers[0].NextAsync()));
    }
}
