using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.GameData;
using RO2.Persistence.Mongo.Persistence;
using RO2.Persistence.Mongo.Repositories;
using RO2.Network.Protocol;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;
using Xunit.Abstractions;
using static RO2.Tests.Packets.ReauditShared;
using RO2.Inventory.Domain;
using RO2.Inventory.Infrastructure.Mongo;

namespace RO2.Tests.Packets;

/// <summary>
/// SP0-INV PASS 1c: the 1324 ReqPCItemSwap write sequence is index-safe (no transient two rows on one cell) and
/// the client-visible 2327 answer is byte-identical to a plain two-write swap. The unit test drives the real
/// handler over a loopback session with an in-memory bag (no Mongo); the integration test runs the same handler
/// against a real Mongo with the unique placement index active and skips cleanly when Mongo is unreachable.
/// </summary>
public sealed class ItemSwapIndexSafeTests
{
    private const uint CharId = 918273u;
    private const ulong SrcUid = 0x0E043DA80E610001UL;
    private const ulong DstUid = 0x0E043DA80E610002UL;

    static ItemSwapIndexSafeTests() => DyeColorBson.Register();

    /// <summary>A 1324 payload: the compact envelope [u8 width=1][u8 len][blob]; the blob is the two 11-byte
    /// descriptors the handler reads (SRC @8, DST @19), padded to the 30 bytes the length check needs.</summary>
    private static byte[] BuildSwap1324(byte srcSlot, byte srcBag, ulong srcUid, byte srcQty,
                                        byte dstSlot, byte dstBag, ulong dstUid)
    {
        var blob = new byte[30];
        blob[2] = 0xA8; blob[3] = 0xE2;       // msgTag 0xE2A8 (INV-H12)
        blob[4] = 0x11;                       // actor handle == the tests' PlayerState.EntityId (0x11)
        blob[8] = srcSlot; blob[9] = srcBag;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(blob.AsSpan(10), srcUid);
        blob[18] = srcQty;
        blob[19] = dstSlot; blob[20] = dstBag;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(blob.AsSpan(21), dstUid);
        var payload = new byte[2 + blob.Length];
        payload[0] = 1;                       // CompactLen width marker
        payload[1] = (byte)blob.Length;       // declared length (lenient; not compared)
        blob.CopyTo(payload.AsSpan(2));
        return payload;
    }

    /// <summary>The 2327 OnSwapItem body the plain non-equip/non-bag swap builds: VarBlob32 { u16 result=0,
    /// PRIMARY = source item at its new cell, VecU16(1) { DEST item at its new cell } }.</summary>
    private static byte[] ExpectedSwap2327(InventoryItem primaryAtNewCell, InventoryItem vectorAtNewCell)
    {
        var p = WorldEntryService.BuildItemRecord126(primaryAtNewCell);
        var v = WorldEntryService.BuildItemRecord126(vectorAtNewCell);
        var body = new List<byte>();
        body.AddRange(BitConverter.GetBytes((uint)(2 + p.Length + 2 + v.Length)));  // VarBlob32 length (LE)
        body.AddRange(BitConverter.GetBytes((ushort)0));                            // result = success
        body.AddRange(p);
        body.AddRange(BitConverter.GetBytes((ushort)1));                            // vector count
        body.AddRange(v);
        return body.ToArray();
    }

    private static (PlayerStateManager players, BroadcastService broadcast) Wire(Harness net)
    {
        var players = new PlayerStateManager(new NoDirtySink());
        var player = new PlayerState { SessionId = net.Sessions[0].SessionId, CharacterId = CharId, Name = "Mover", EntityId = 0x11u };
        players.AddPlayer(player.SessionId, player);
        var broadcast = new BroadcastService(players, NullLogger<BroadcastService>.Instance);
        broadcast.SetServer(net.Server);
        return (players, broadcast);
    }

    private static ItemSwapHandler Build(IInventoryRepository inv, PlayerStateManager players, BroadcastService broadcast)
    {
        var services = new ServiceCollection()
            .AddScoped(_ => inv)
            .AddSingleton(new GameDataStore())
            .BuildServiceProvider();
        return new ItemSwapHandler(services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ItemSwapHandler>.Instance, players, broadcast);
    }

    [Fact]
    public async Task Swap_YieldsTheSwappedSlots_AndTheByteExact2327()
    {
        await using var net = await Harness.StartAsync(1);
        var (players, broadcast) = Wire(net);

        // Two non-stackable rows in the main bag: src at flat 0 (client bag 1 slot 50), dst at flat 1 (slot 51).
        var src = new InventoryItem { Id = SrcUid, CharacterId = CharId, ItemId = 1001, Count = 1, StorageType = 0, Slot = 0 };
        var dst = new InventoryItem { Id = DstUid, CharacterId = CharId, ItemId = 1002, Count = 1, StorageType = 0, Slot = 1 };
        var bag = new GearInventory([src, dst]);
        var handler = Build(bag, players, broadcast);

        await handler.HandleAsync(net.Sessions[0], BuildSwap1324(50, 1, SrcUid, 1, 51, 1, DstUid));

        var (op, body) = await net.Readers[0].NextAsync();
        Assert.Equal(ZoneS2COpcodes.OnSwapItem, op);

        // Final slots swapped, both rows still present (no duplicate, no loss).
        Assert.Equal((short)1, bag.Items.Single(i => i.Id == SrcUid).Slot);
        Assert.Equal((short)0, bag.Items.Single(i => i.Id == DstUid).Slot);
        Assert.Equal(2, bag.Items.Count);

        // The 2327 answer is exactly PRIMARY=src-at-new-cell, VECTOR=dst-at-new-cell — the oracle a future
        // write-order change must not alter.
        Assert.Equal(ExpectedSwap2327(src, dst), body);
    }

    // ---- integration: the real handler against a real Mongo with the unique placement index active -------------

    private const string ConnectionString = "mongodb://localhost:27017";

    private readonly ITestOutputHelper _output;
    public ItemSwapIndexSafeTests(ITestOutputHelper output) => _output = output;

    private bool MongoUp()
    {
        try
        {
            var settings = MongoClientSettings.FromConnectionString(ConnectionString);
            settings.ServerSelectionTimeout = TimeSpan.FromSeconds(2);
            _ = new MongoClient(settings).ListDatabaseNames(new ListDatabaseNamesOptions());
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or MongoException)
        {
            _output.WriteLine($"SKIP: Mongo unreachable at {ConnectionString} ({ex.GetType().Name}).");
            return false;
        }
    }

    [Fact]
    public async Task UniqueIndex_RejectsASecondRowInAnOccupiedCell_AndTheSwapThroughTheHandlerSucceeds()
    {
        if (!MongoUp()) return;

        var dbName = "ro2_test_swap_" + Guid.NewGuid().ToString("N");
        var client = new MongoClient(ConnectionString);
        try
        {
            var context = new MongoDbContext(ConnectionString, dbName);
            var repo = new InventoryRepository(context);
            var items = context.GetCollection<InventoryItem>("inventory_items");

            Assert.True(await repo.EnsureIndexesAsync());   // index active

            await items.InsertOneAsync(new InventoryItem { Id = SrcUid, CharacterId = CharId, ItemId = 1001, Count = 1, StorageType = 0, Slot = 0 });
            await items.InsertOneAsync(new InventoryItem { Id = DstUid, CharacterId = CharId, ItemId = 1002, Count = 1, StorageType = 0, Slot = 1 });

            // The index rejects a second row dropped into an occupied (char, storage, slot) — the collision the
            // naive "write source into the still-occupied destination" order would have hit.
            await Assert.ThrowsAsync<MongoWriteException>(() =>
                items.InsertOneAsync(new InventoryItem { Id = 3, CharacterId = CharId, ItemId = 1003, Count = 1, StorageType = 0, Slot = 1 }));

            // A legit swap through the real handler succeeds against the live index (park-then-place order).
            await using var net = await Harness.StartAsync(1);
            var (players, broadcast) = Wire(net);
            var handler = Build(repo, players, broadcast);

            await handler.HandleAsync(net.Sessions[0], BuildSwap1324(50, 1, SrcUid, 1, 51, 1, DstUid));
            var (op, _) = await net.Readers[0].NextAsync();
            Assert.Equal(ZoneS2COpcodes.OnSwapItem, op);

            var rows = await items.Find(i => i.CharacterId == CharId).ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.Equal((short)1, rows.Single(r => r.Id == SrcUid).Slot);   // src moved to the dest cell
            Assert.Equal((short)0, rows.Single(r => r.Id == DstUid).Slot);   // dest settled into the source cell
            Assert.All(rows, r => Assert.True(r.Slot >= 0));                 // nothing left parked at -1
        }
        finally
        {
            await client.DropDatabaseAsync(dbName);
        }
    }
}
