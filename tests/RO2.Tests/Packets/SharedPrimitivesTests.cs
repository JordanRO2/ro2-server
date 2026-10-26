using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Persistence.Mongo.Persistence;
using RO2.Persistence.Mongo.Repositories;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using RO2.Worlds.Domain;
using Xunit;
using Xunit.Abstractions;
using RO2.Inventory.Domain;
using RO2.Inventory.Infrastructure.Mongo;

namespace RO2.Tests.Packets;

/// <summary>
/// SHARED-A economy primitives, the parts that need no Postgres: <see cref="ActorGate.CanTransact"/>, the interface
/// default bodies (what test fakes get), the filters/updates the Mongo repositories send, and a Mongo integration
/// proof of the atomic claim and re-home (skips cleanly when Mongo is unreachable).
/// </summary>
public sealed class SharedPrimitivesTests
{
    static SharedPrimitivesTests() => DyeColorBson.Register();

    private const uint Map = 1100;

    private static PlayerState Hero(float x = 0, float y = 0, uint map = Map, bool dead = false) =>
        new() { MapId = map, X = x, Y = y, IsDead = dead };

    private static NpcRuntimeState Npc(float x = 0, float y = 0, uint map = Map, bool dead = false) =>
        new() { MapId = map, PosX = x, PosY = y, IsDead = dead };

    // ---- ActorGate.CanTransact ----

    [Fact]
    public void CanTransact_passes_when_alive_same_map_and_in_reach()
        => Assert.Equal(ActorRefusal.None,
            ActorGate.CanTransact(Hero(), Npc(3, 4), ActorGate.NpcInteractReach));

    [Fact]
    public void CanTransact_allows_a_dead_anchor_but_not_a_dead_player()
    {
        Assert.Equal(ActorRefusal.None, ActorGate.CanTransact(Hero(), Npc(dead: true), ActorGate.NpcInteractReach));
        Assert.Equal(ActorRefusal.ActorDead, ActorGate.CanTransact(Hero(dead: true), Npc(), ActorGate.NpcInteractReach));
    }

    [Fact]
    public void CanTransact_refuses_unresolved_anchor_cross_map_and_out_of_reach()
    {
        Assert.Equal(ActorRefusal.TargetNotFound, ActorGate.CanTransact(Hero(), null, ActorGate.NpcInteractReach));
        Assert.Equal(ActorRefusal.CrossMap, ActorGate.CanTransact(Hero(), Npc(map: Map + 1), ActorGate.NpcInteractReach));
        Assert.Equal(ActorRefusal.OutOfRange,
            ActorGate.CanTransact(Hero(), Npc(ActorGate.NpcInteractReach + 1, 0), ActorGate.NpcInteractReach));
    }

    [Fact]
    public void CanTransact_is_nan_safe_and_reach_is_inclusive()
    {
        Assert.Equal(ActorRefusal.OutOfRange, ActorGate.CanTransact(Hero(float.NaN, 0), Npc(), 10f));
        Assert.Equal(ActorRefusal.OutOfRange, ActorGate.CanTransact(Hero(), Npc(float.NaN, 0), 10f));
        Assert.Equal(ActorRefusal.OutOfRange, ActorGate.CanTransact(Hero(), Npc(), float.NaN));
        Assert.Equal(ActorRefusal.None, ActorGate.CanTransact(Hero(), Npc(10, 0), 10f));
    }

    // ---- interface default bodies (the non-atomic fallback test fakes get) ----

    [Fact]
    public async Task Inventory_default_TryReassign_checks_the_owner()
    {
        var repo = new FakeInventory();
        repo.Rows.Add(new InventoryItem { Id = 1, CharacterId = 10, Slot = 1 });
        IInventoryRepository i = repo;

        Assert.False(await i.TryReassignAsync(1, 99, 20, 0, 3));
        Assert.False(await i.TryReassignAsync(2, 10, 20, 0, 3));
        Assert.True(await i.TryReassignAsync(1, 10, 20, 2, 3));
        var row = repo.Rows.Single();
        Assert.Equal(20u, row.CharacterId);
        Assert.Equal((byte)2, row.StorageType);
        Assert.Equal((short)3, row.Slot);
        Assert.False(await i.TryReassignAsync(1, 10, 30, 0, 0)); // old owner can no longer move it
    }

    [Fact]
    public async Task Mail_default_TryClaim_yields_exactly_one_non_null_sequentially()
    {
        var repo = new FakeMail();
        repo.Mails.Add(new MailMessage { Id = 1, Uid = 77, ReceiverCharId = 5, AttachedItemId = 501, Gold = 40 });
        IMailRepository m = repo;

        Assert.Null(await m.TryClaimItemAsync(6, 77));
        var item = await m.TryClaimItemAsync(5, 77);
        Assert.NotNull(item);
        Assert.True(item!.ItemClaimed);
        Assert.Null(await m.TryClaimItemAsync(5, 77));

        Assert.NotNull(await m.TryClaimGoldAsync(5, 77));
        Assert.Null(await m.TryClaimGoldAsync(5, 77));
        Assert.Null(await m.TryClaimItemAsync(5, 12345)); // missing
    }

    [Fact]
    public async Task Mail_default_TryClaim_refuses_when_nothing_is_attached()
    {
        var repo = new FakeMail();
        repo.Mails.Add(new MailMessage { Id = 1, Uid = 1, ReceiverCharId = 5 });
        IMailRepository m = repo;

        Assert.Null(await m.TryClaimItemAsync(5, 1));
        Assert.Null(await m.TryClaimGoldAsync(5, 1));
    }

    // ---- what the Mongo repositories send ----

    [Fact]
    public void Mongo_reassign_update_sets_exactly_owner_storage_and_slot()
    {
        var doc = InventoryRepository.BuildReassignUpdate(20u, 2, 9)
            .Render(new RenderArgs<InventoryItem>(
                BsonSerializer.SerializerRegistry.GetSerializer<InventoryItem>(), BsonSerializer.SerializerRegistry))
            .AsBsonDocument;
        var set = doc["$set"].AsBsonDocument;
        Assert.Equal(new[] { "CharacterId", "StorageType", "Slot" }, set.Names.ToArray());
        Assert.Equal(20L, set["CharacterId"].ToInt64());
        Assert.Equal(2, set["StorageType"].ToInt32());
        Assert.Equal(9, set["Slot"].ToInt32());
    }

    [Fact]
    public void Mongo_claim_filters_guard_on_receiver_uid_unclaimed_and_attachment()
    {
        static BsonDocument R(FilterDefinition<MailMessage> f) => f.Render(new RenderArgs<MailMessage>(
            BsonSerializer.SerializerRegistry.GetSerializer<MailMessage>(), BsonSerializer.SerializerRegistry));

        var item = R(MailRepository.BuildClaimItemFilter(5, 77)).ToJson();
        Assert.Contains("ReceiverCharId", item);
        Assert.Contains("Uid", item);
        Assert.Contains("ItemClaimed", item);
        Assert.Contains("$ne", item); // AttachedItemId != 0

        var gold = R(MailRepository.BuildClaimGoldFilter(5, 77)).ToJson();
        Assert.Contains("GoldClaimed", gold);
        Assert.Contains("$gt", gold); // Gold > 0
    }

    // ---- Mongo integration (skips when Mongo is unreachable) ----

    private const string ConnectionString = "mongodb://localhost:27017";
    private readonly ITestOutputHelper _output;

    public SharedPrimitivesTests(ITestOutputHelper output) => _output = output;

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

    private static async Task WithFreshDatabase(Func<MongoDbContext, Task> body)
    {
        var dbName = "ro2_test_shared_a_" + Guid.NewGuid().ToString("N");
        try { await body(new MongoDbContext(ConnectionString, dbName)); }
        finally { await new MongoClient(ConnectionString).DropDatabaseAsync(dbName); }
    }

    [Fact]
    public async Task Mongo_TryReassign_owner_filter_and_concurrent_distinct_slots()
    {
        if (!MongoUp()) return;
        await WithFreshDatabase(async ctx =>
        {
            var repo = new InventoryRepository(ctx);
            Assert.True(await repo.EnsureIndexesAsync());
            var items = Enumerable.Range(0, 8)
                .Select(n => new InventoryItem { CharacterId = 1, ItemId = 500, Slot = (short)n, StorageType = 0 }).ToList();
            foreach (var i in items) await repo.AddItemAsync(i);

            Assert.False(await repo.TryReassignAsync(items[0].Id, 99, 2, 0, 20)); // wrong owner
            Assert.Equal(1u, (await repo.GetByIdAsync(items[0].Id))!.CharacterId);

            var moved = await Task.WhenAll(items.Select((i, n) => repo.TryReassignAsync(i.Id, 1, 2, 0, (short)(10 + n))));
            Assert.All(moved, Assert.True);
            var dest = await repo.GetByCharacterAsync(2);
            Assert.Equal(8, dest.Count);
            Assert.Equal(8, dest.Select(r => r.Slot).Distinct().Count());
            Assert.Empty(await repo.GetByCharacterAsync(1));

            // the same (now moved) row raced by old-owner senders: nobody wins
            var again = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => repo.TryReassignAsync(items[0].Id, 1, 3, 0, 0)));
            Assert.DoesNotContain(true, again);
        });
    }

    [Fact]
    public async Task Mongo_TryClaim_double_claim_yields_exactly_one_non_null()
    {
        if (!MongoUp()) return;
        await WithFreshDatabase(async ctx =>
        {
            var repo = new MailRepository(ctx);
            var both = await repo.AddAsync(new MailMessage { ReceiverCharId = 5, AttachedItemId = 501, Gold = 900 });

            var items = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => repo.TryClaimItemAsync(5, both.Uid)));
            var golds = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => repo.TryClaimGoldAsync(5, both.Uid)));

            Assert.Equal(1, items.Count(r => r is not null));
            Assert.Equal(1, golds.Count(r => r is not null));
            Assert.True(items.Single(r => r is not null)!.ItemClaimed);
            var stored = (await repo.GetByUidAsync(5, both.Uid))!;
            Assert.True(stored.ItemClaimed && stored.GoldClaimed);

            Assert.Null(await repo.TryClaimItemAsync(6, both.Uid)); // wrong receiver
            var empty = await repo.AddAsync(new MailMessage { ReceiverCharId = 5 });
            Assert.Null(await repo.TryClaimItemAsync(5, empty.Uid));
            Assert.Null(await repo.TryClaimGoldAsync(5, empty.Uid));
        });
    }

    private sealed class FakeInventory : IInventoryRepository
    {
        public List<InventoryItem> Rows { get; } = new();
        public Task<IReadOnlyList<InventoryItem>> GetByCharacterAsync(uint characterId) =>
            Task.FromResult<IReadOnlyList<InventoryItem>>(Rows.Where(r => r.CharacterId == characterId).ToList());
        public Task<IReadOnlyList<InventoryItem>> GetEquipmentAsync(uint characterId) =>
            Task.FromResult<IReadOnlyList<InventoryItem>>(new List<InventoryItem>());
        public Task<InventoryItem?> GetByIdAsync(ulong id) => Task.FromResult(Rows.FirstOrDefault(r => r.Id == id));
        public Task AddItemAsync(InventoryItem item) { Rows.Add(item); return Task.CompletedTask; }
        public Task RemoveItemAsync(ulong itemId) { Rows.RemoveAll(r => r.Id == itemId); return Task.CompletedTask; }
        public Task UpdateItemAsync(InventoryItem item) => Task.CompletedTask; // rows are the same instances
        public Task<ulong> GetNextIdAsync() => Task.FromResult(1UL);
        public Task<int> RepairSlotsAsync(uint characterId) => Task.FromResult(0);
    }

    private sealed class FakeMail : IMailRepository
    {
        public List<MailMessage> Mails { get; } = new();
        public Task<MailMessage> AddAsync(MailMessage draft, CancellationToken ct = default) { Mails.Add(draft); return Task.FromResult(draft); }
        public Task<IReadOnlyList<MailMessage>> GetInboxAsync(uint receiverCharId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<MailMessage>>(Mails.Where(m => m.ReceiverCharId == receiverCharId).ToList());
        public Task<MailMessage?> GetByUidAsync(uint receiverCharId, ulong uid, CancellationToken ct = default) =>
            Task.FromResult(Mails.FirstOrDefault(m => m.ReceiverCharId == receiverCharId && m.Uid == uid));
        public Task<long> CountUnreadAsync(uint receiverCharId, CancellationToken ct = default) =>
            Task.FromResult((long)Mails.Count(m => m.ReceiverCharId == receiverCharId && !m.IsRead));
        public Task<long> CountLiveInboxAsync(uint receiverCharId, DateTime notBefore, CancellationToken ct = default) =>
            Task.FromResult((long)Mails.Count(m => m.ReceiverCharId == receiverCharId && m.SentAt >= notBefore));
        public Task ReplaceAsync(MailMessage mail, CancellationToken ct = default) => Task.CompletedTask; // same instances
        public Task<bool> DeleteAsync(uint receiverCharId, ulong uid, CancellationToken ct = default) =>
            Task.FromResult(Mails.RemoveAll(m => m.ReceiverCharId == receiverCharId && m.Uid == uid) > 0);
        public Task<bool> DeleteIfSpentAsync(uint receiverCharId, ulong uid, CancellationToken ct = default) =>
            Task.FromResult(false);
    }
}
