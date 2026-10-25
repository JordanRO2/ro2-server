using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Persistence.Mongo.Persistence;
using RO2.Application;
using RO2.Persistence.Mongo.Repositories;
using RO2.Server.Zone.Services;
using Xunit;
using Xunit.Abstractions;
using RO2.Inventory.Application;
using RO2.Inventory.Domain;
using RO2.Inventory.Infrastructure.Mongo;
using RO2.Characters.Domain;

namespace RO2.Tests.Packets;

/// <summary>
/// Inventory writes cannot duplicate, collide or reuse ids. Unit tests run without MongoDB: they render the filters and updates the repository sends and exercise the per-character gate. The integration tests at the bottom verify MongoDB atomicity and skip when MongoDB is unreachable.
/// </summary>
public sealed class InventoryRepositoryHardeningTests
{
    static InventoryRepositoryHardeningTests()
    {
        
        
        DyeColorBson.Register();
    }

    private static BsonDocument Render(UpdateDefinition<InventoryItem> update) =>
        update.Render(new RenderArgs<InventoryItem>(
            BsonSerializer.SerializerRegistry.GetSerializer<InventoryItem>(), BsonSerializer.SerializerRegistry))
            .AsBsonDocument;

    private static BsonDocument Render(FilterDefinition<InventoryItem> filter) =>
        filter.Render(new RenderArgs<InventoryItem>(
            BsonSerializer.SerializerRegistry.GetSerializer<InventoryItem>(), BsonSerializer.SerializerRegistry));

    private static BsonDocument Render(UpdateDefinition<BsonDocument> update) =>
        update.Render(new RenderArgs<BsonDocument>(
            BsonSerializer.SerializerRegistry.GetSerializer<BsonDocument>(), BsonSerializer.SerializerRegistry))
            .AsBsonDocument;

    
    [Fact]
    public void IdCounter_IsAServerSideIncrementByOne_NotAReadThenAdd()
    {
        var doc = Render(InventoryRepository.BuildCounterIncrement());
        Assert.Equal(new BsonDocument("$inc", new BsonDocument("seq", 1L)), doc);
    }

    [Fact]
    public void IdCounterSeed_OnlyAppliesOnInsert_SoItNeverLowersAnAdvancedCounter()
    {
        var doc = Render(InventoryRepository.BuildCounterSeed(41UL));
        Assert.Equal(new BsonDocument("$setOnInsert", new BsonDocument("seq", 41L)), doc);
    }

    
    [Theory]
    [InlineData(new int[0], 0)]
    [InlineData(new[] { 0, 1, 2 }, 3)]
    [InlineData(new[] { 0, 2, 3 }, 1)]
    [InlineData(new[] { -1, 1 }, 0)]
    public void LowestFreeSlot_PicksTheFirstUnoccupiedIndex(int[] occupied, int expected)
    {
        Assert.Equal((short)expected, InventoryRepository.LowestFreeSlot(occupied));
    }

    
    [Fact]
    public void RemoveFilter_NamesBothTheItemAndTheOwner()
    {
        var doc = Render(InventoryRepository.BuildOwnedFilter(77UL, 5u));
        var text = doc.ToString();
        Assert.Contains("_id", text);
        Assert.Contains("CharacterId", text);
        Assert.Contains("77", text);
        Assert.Contains("5", text);
    }

    [Fact]
    public void AdjustCount_IsAnIncOfCountOnly()
    {
        var doc = Render(InventoryRepository.BuildAdjustCountUpdate(-3));
        Assert.Equal(new BsonDocument("$inc", new BsonDocument("Count", -3)), doc);
    }

    [Fact]
    public void AdjustCount_NegativeDelta_IsGuardedByTheStackSize()
    {
        var text = Render(InventoryRepository.BuildAdjustCountFilter(9UL, 2u, -4)).ToString();
        Assert.Contains("$gte", text);
        Assert.Contains("Count", text);
        Assert.Contains("CharacterId", text);
    }

    [Fact]
    public void AdjustCount_PositiveDelta_NeedsNoStackGuard()
    {
        var text = Render(InventoryRepository.BuildAdjustCountFilter(9UL, 2u, 4)).ToString();
        Assert.DoesNotContain("$gte", text);
        Assert.Contains("CharacterId", text);
    }

    [Fact]
    public void ItemUpdate_IsAFieldLevelSet_NeverTouchingTheImmutableFields()
    {
        var item = new InventoryItem
        {
            Id = 10, CharacterId = 3, ItemId = 501, Count = 7, Slot = 4, StorageType = 0, Refine = 2,
            DyeColor = 0xFF272725u, CardsJson = "[1]",
        };

        var set = Render(InventoryRepository.BuildItemUpdate(item))["$set"].AsBsonDocument;

        Assert.Equal(7, set["Count"].ToInt32());
        Assert.Equal(4, set["Slot"].ToInt32());
        Assert.Equal(2, set["Refine"].ToInt32());
        Assert.Equal(0xFF272725L, set["DyeColor"].ToInt64());
        Assert.Equal("[1]", set["CardsJson"].AsString);
        Assert.False(set.Contains("_id"));
        Assert.False(set.Contains("CreatedAt"));
        Assert.False(set.Contains("Character"));
    }

    [Fact]
    public void ItemUpdate_CoversEveryPersistedField()
    {
        
        var set = Render(InventoryRepository.BuildItemUpdate(new InventoryItem()))["$set"].AsBsonDocument;
        var persisted = BsonClassMap.LookupClassMap(typeof(InventoryItem)).AllMemberMaps
            .Select(m => m.ElementName)
            .Where(n => n is not ("_id" or "CreatedAt" or "Character"))
            .ToHashSet();
        Assert.Equal(persisted.OrderBy(n => n), set.Names.OrderBy(n => n));
    }

    
    [Fact]
    public async Task DefaultRemove_RefusesAnotherCharactersRow()
    {
        var repo = new FakeRepo();
        repo.Rows.Add(new InventoryItem { Id = 1, CharacterId = 10, Count = 5 });
        IInventoryRepository inv = repo;

        Assert.False(await inv.RemoveItemAsync(1UL, 99u));
        Assert.Single(repo.Rows);
        Assert.True(await inv.RemoveItemAsync(1UL, 10u));
        Assert.Empty(repo.Rows);
    }

    [Fact]
    public async Task DefaultAdjustCount_NeverGoesBelowZeroAndChecksTheOwner()
    {
        var repo = new FakeRepo();
        repo.Rows.Add(new InventoryItem { Id = 1, CharacterId = 10, Count = 5 });
        IInventoryRepository inv = repo;

        Assert.Null(await inv.AdjustCountAsync(1UL, 10u, -6));
        Assert.Null(await inv.AdjustCountAsync(1UL, 99u, -1));
        Assert.Equal(5, repo.Rows[0].Count);
        Assert.Equal(2, await inv.AdjustCountAsync(1UL, 10u, -3));
    }

    
    [Fact]
    public async Task Gate_SerialisesAReadModifyWritePerCharacter()
    {
        const uint character = 4242u;
        var shared = 0;
        
        async Task<int> Increment()
        {
            var read = shared;
            await Task.Delay(5);
            shared = read + 1;
            return 0;
        }

        await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => CharacterInventoryGate.RunAsync(character, Increment)));

        Assert.Equal(20, shared);
    }

    [Fact]
    public async Task Gate_DoesNotMakeDifferentCharactersWaitOnEachOther()
    {
        var release = new TaskCompletionSource();
        var held = CharacterInventoryGate.RunAsync(1001u, async () => { await release.Task; return 0; });

        var other = CharacterInventoryGate.RunAsync(1002u, () => Task.FromResult(7));
        Assert.Equal(7, await other.WaitAsync(TimeSpan.FromSeconds(5)));

        release.SetResult();
        await held;
    }

    [Fact]
    public async Task Gate_ReleasesWhenTheBodyThrows()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CharacterInventoryGate.RunAsync<int>(2001u, () => throw new InvalidOperationException()));

        Assert.Equal(1, await CharacterInventoryGate.RunAsync(2001u, () => Task.FromResult(1))
            .WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private sealed class FakeRepo : IInventoryRepository
    {
        public List<InventoryItem> Rows { get; } = new();
        public Task<IReadOnlyList<InventoryItem>> GetByCharacterAsync(uint characterId) =>
            Task.FromResult<IReadOnlyList<InventoryItem>>(Rows.Where(r => r.CharacterId == characterId).ToList());
        public Task<IReadOnlyList<InventoryItem>> GetEquipmentAsync(uint characterId) =>
            Task.FromResult<IReadOnlyList<InventoryItem>>(new List<InventoryItem>());
        public Task<InventoryItem?> GetByIdAsync(ulong id) => Task.FromResult(Rows.FirstOrDefault(r => r.Id == id));
        public Task AddItemAsync(InventoryItem item) { Rows.Add(item); return Task.CompletedTask; }
        public Task RemoveItemAsync(ulong itemId) { Rows.RemoveAll(r => r.Id == itemId); return Task.CompletedTask; }
        public Task UpdateItemAsync(InventoryItem item) => Task.CompletedTask;
        public Task<ulong> GetNextIdAsync() => Task.FromResult(1UL);
        public Task<int> RepairSlotsAsync(uint characterId) => Task.FromResult(0);
    }
}

/// <summary>
/// Integration checks of the MongoDB-side guarantees (atomic id counter, unique slot index). Runs against a throwaway database on <c>localhost:27017</c>, dropped afterwards; skips when MongoDB is unreachable.
/// </summary>
public sealed class InventoryRepositoryMongoIntegrationTests
{
    private const string ConnectionString = "mongodb://localhost:27017";

    private readonly ITestOutputHelper _output;

    public InventoryRepositoryMongoIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        DyeColorBson.Register();
    }

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

    private static async Task WithFreshDatabase(Func<InventoryRepository, IMongoCollection<InventoryItem>, Task> body)
    {
        var dbName = "ro2_test_inv_" + Guid.NewGuid().ToString("N");
        var client = new MongoClient(ConnectionString);
        try
        {
            var context = new MongoDbContext(ConnectionString, dbName);
            await body(new InventoryRepository(context), context.GetCollection<InventoryItem>("inventory_items"));
        }
        finally
        {
            await client.DropDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task ParallelAddItem_UnplacedItems_GetDistinctIdsAndDistinctSlots()
    {
        if (!MongoUp()) return;

        await WithFreshDatabase(async (repo, items) =>
        {
            var batch = Enumerable.Range(0, 40)
                .Select(n => new InventoryItem { CharacterId = 1, ItemId = (uint)(500 + n), Slot = -1, StorageType = 0 })
                .ToList();

            await Task.WhenAll(batch.Select(i => repo.AddItemAsync(i)));

            var rows = await items.Find(_ => true).ToListAsync();
            Assert.Equal(40, rows.Count);
            Assert.Equal(40, rows.Select(r => r.Id).Distinct().Count());
            Assert.Equal(40, rows.Select(r => r.Slot).Distinct().Count());
            Assert.All(rows, r => Assert.True(r.Slot >= 0 && r.Id > 0));
        });
    }

    [Fact]
    public async Task ParallelGetNextId_NeverReturnsTheSameIdTwice()
    {
        if (!MongoUp()) return;

        await WithFreshDatabase(async (repo, _) =>
        {
            var ids = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => repo.GetNextIdAsync()));
            Assert.Equal(50, ids.Distinct().Count());
        });
    }

    [Fact]
    public async Task UniqueSlotIndex_RejectsASecondRowInTheSameSlot_ButNotUnplacedRows()
    {
        if (!MongoUp()) return;

        await WithFreshDatabase(async (repo, items) =>
        {
            Assert.True(await repo.EnsureIndexesAsync());
            Assert.True(await repo.EnsureIndexesAsync()); 

            await items.InsertOneAsync(new InventoryItem { Id = 1, CharacterId = 1, StorageType = 0, Slot = 3 });
            await Assert.ThrowsAsync<MongoWriteException>(() =>
                items.InsertOneAsync(new InventoryItem { Id = 2, CharacterId = 1, StorageType = 0, Slot = 3 }));

            
            await items.InsertOneAsync(new InventoryItem { Id = 3, CharacterId = 1, StorageType = 2, Slot = 3 });
            await items.InsertOneAsync(new InventoryItem { Id = 4, CharacterId = 2, StorageType = 0, Slot = 3 });
            await items.InsertOneAsync(new InventoryItem { Id = 5, CharacterId = 1, StorageType = 0, Slot = -1 });
            await items.InsertOneAsync(new InventoryItem { Id = 6, CharacterId = 1, StorageType = 0, Slot = -1 });
        });
    }

    [Fact]
    public async Task AdjustCountAndOwnedRemove_AreAtomicAndOwnerChecked()
    {
        if (!MongoUp()) return;

        await WithFreshDatabase(async (repo, items) =>
        {
            await items.InsertOneAsync(new InventoryItem { Id = 1, CharacterId = 1, Count = 10, Slot = 0 });

            var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => repo.AdjustCountAsync(1UL, 1u, -4)));
            Assert.Equal(2, results.Count(r => r.HasValue));            
            Assert.Equal(2, (await items.Find(i => i.Id == 1).FirstAsync()).Count);
            Assert.Null(await repo.AdjustCountAsync(1UL, 99u, 1));      

            Assert.False(await repo.RemoveItemAsync(1UL, 99u));
            Assert.True(await repo.RemoveItemAsync(1UL, 1u));
        });
    }
}
