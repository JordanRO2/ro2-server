using MongoDB.Driver;
using RO2.Domain.Entities;
using RO2.Persistence.Mongo.Persistence;
using Xunit;
using MongoInventoryRepository = RO2.Inventory.Infrastructure.Mongo.InventoryRepository;
using PgInventoryRepository = RO2.Inventory.Infrastructure.Postgres.InventoryRepository;
using RO2.Inventory.Domain;

namespace RO2.Tests.Integration;

/// <summary>
/// Characterization of <c>RepairSlotsAsync</c>'s placement decision, which both providers share: per storage type
/// (worn rows excluded) the first claimant of a slot, in ascending slot then id order with unplaced rows last, keeps
/// it, and every duplicate or unplaced row takes the next free index counting up from 0. Mongo (no unique index in a
/// scratch database) is driven with real collisions; Postgres, whose schema enforces the index, with unplaced rows.
/// Both soft-skip when their server is not there.
/// </summary>
[Collection("Postgres")]
public sealed class BagSlotRepairTests
{
    private readonly PostgresFixture _fx;
    public BagSlotRepairTests(PostgresFixture fx) => _fx = fx;

    private const string MongoConnection = "mongodb://localhost:27017";

    private static InventoryItem Row(ulong id, short slot, byte storage = 0)
        => new() { Id = id, CharacterId = 41, ItemId = 500, Count = 1, Slot = slot, StorageType = storage };

    private static bool MongoUp()
    {
        try
        {
            var settings = MongoClientSettings.FromConnectionString(MongoConnection);
            settings.ServerSelectionTimeout = TimeSpan.FromSeconds(2);
            _ = new MongoClient(settings).ListDatabaseNames(new ListDatabaseNamesOptions());
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or MongoException)
        {
            return false;
        }
    }

    [Fact]
    public async Task Mongo_RepairSlots_FirstClaimantKeeps_DuplicatesAndUnplacedTakeTheLowestFree_PerStorage_WornUntouched()
    {
        if (!MongoUp()) return;
        var dbName = "ro2_test_repair_" + Guid.NewGuid().ToString("N");
        try
        {
            var context = new MongoDbContext(MongoConnection, dbName);
            var items = context.GetCollection<InventoryItem>("inventory_items");
            await items.InsertManyAsync([
                Row(1, 0), Row(2, 2), Row(3, 2), Row(4, 2), Row(5, -1),        // bag
                Row(6, 0, storage: 2), Row(7, 0, storage: 2),                  // bank
                Row(8, 5, storage: 1), Row(9, 5, storage: 1),                  // worn: colliding, never reassigned
            ]);
            var repo = new MongoInventoryRepository(context);

            Assert.Equal(4, await repo.RepairSlotsAsync(41));

            var slots = (await items.Find(_ => true).ToListAsync()).ToDictionary(r => r.Id, r => r.Slot);
            Assert.Equal(new Dictionary<ulong, short>
            {
                [1] = 0, [2] = 2, [3] = 1, [4] = 3, [5] = 4,
                [6] = 0, [7] = 1,
                [8] = 5, [9] = 5,
            }, slots);
            Assert.Equal(0, await repo.RepairSlotsAsync(41));
        }
        finally
        {
            await new MongoClient(MongoConnection).DropDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task Postgres_RepairSlots_UnplacedRowsTakeTheLowestFreeIndexInIdOrder()
    {
        if (!_fx.Available) return;
        var repo = new PgInventoryRepository(_fx.Db!);
        var ch = (uint)Random.Shared.NextInt64(3_000_000_000L, 4_200_000_000L);
        InventoryItem New(short slot) => new() { CharacterId = ch, ItemId = 500, Count = 1, Slot = slot, StorageType = 0 };
        var a = New(0); var b = New(3); var c = New(1); var d = New(2);
        foreach (var i in new[] { a, b, c, d }) await repo.AddItemAsync(i);
        c.Slot = -1; d.Slot = -1;
        await repo.UpdateItemAsync(c);
        await repo.UpdateItemAsync(d);

        Assert.Equal(2, await repo.RepairSlotsAsync(ch));

        Assert.Equal((short)0, (await repo.GetByIdAsync(a.Id))!.Slot);
        Assert.Equal((short)3, (await repo.GetByIdAsync(b.Id))!.Slot);
        Assert.Equal((short)1, (await repo.GetByIdAsync(c.Id))!.Slot);
        Assert.Equal((short)2, (await repo.GetByIdAsync(d.Id))!.Slot);
    }
}
