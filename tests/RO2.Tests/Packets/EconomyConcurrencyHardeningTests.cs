using System.Collections.Frozen;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.GameData;
using RO2.Persistence.Mongo.Persistence;
using RO2.Persistence.Mongo.Repositories;
using RO2.Network.Protocol;
using RO2.Network.ProudNet;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Economy.Domain;
using RO2.Server.Zone.Services;
using Xunit;
using Xunit.Abstractions;
using static RO2.Tests.Packets.ReauditShared;
using RO2.Inventory.Domain;
using RO2.Inventory.Infrastructure.Mongo;
using RO2.Characters.Domain;

namespace RO2.Tests.Packets;

/// <summary>
/// HSV PASS 1g (economy backlog E-H3): the item-side economy mutations are serialized per character and
/// written with atomic, owner-checked ops, so a SECOND concurrent session cannot dupe or roll back items.
/// These drive the real SellItemHandler, TradeConfirmHandler and GuildService.DepositAsync over a loopback
/// session with a thread-safe in-memory bag that models Mongo's atomic owner-checked writes; the single
/// Mongo integration test at the bottom proves the same owner-change primitive against a real Mongo and
/// skips cleanly when none is reachable (the InventoryRepositoryHardeningTests pattern).
/// </summary>
public sealed class EconomyConcurrencyHardeningTests
{
    private const uint CharA = 7_001_001u;
    private const uint CharB = 7_001_002u;
    private const uint HandleA = 0x11_01_11u;
    private const uint HandleB = 0x12_01_11u;

    // A non-stackable item worth 100z to sell, and a stackable one worth 10z/unit.
    private const uint SingleItem = 5001u;
    private const uint StackItem = 5002u;
    private const uint SinglePrice = 100u;
    private const uint StackPrice = 10u;

    static EconomyConcurrencyHardeningTests() => DyeColorBson.Register();

    private readonly ITestOutputHelper _output;
    public EconomyConcurrencyHardeningTests(ITestOutputHelper output) => _output = output;

    // ---- doubles ---------------------------------------------------------------------------------------

    /// <summary>A GameDataStore carrying only the item templates a test needs (Items has a private setter).</summary>
    private static GameDataStore StoreWith(params ItemData[] items)
    {
        var store = new GameDataStore();
        typeof(GameDataStore).GetProperty(nameof(GameDataStore.Items))!
            .SetValue(store, items.ToFrozenDictionary(i => i.ID));
        return store;
    }

    private const uint ShopMapId = 1100;
    private const uint ShopNpcAsset = 10030000;

    // Is_Trade 1: the swap re-runs the tradeable gate on the live row (E-H4). Is_Sell 1: SellItemHandler (E-H6)
    // skips a template whose Is_Sell is 0. A shop NPC asset is registered so TrackSpawns can resolve the handle.
    private static GameDataStore PricedStore()
    {
        var store = StoreWith(
            new ItemData { ID = SingleItem, Price_Sell = SinglePrice, Stack_Max = 1, Is_Trade = 1, Is_Sell = 1 },
            new ItemData { ID = StackItem, Price_Sell = StackPrice, Stack_Max = 99, Is_Trade = 1, Is_Sell = 1 });
        typeof(GameDataStore).GetProperty(nameof(GameDataStore.Npcs))!
            .SetValue(store, new[] { new NpcData { ID = ShopNpcAsset } }.ToFrozenDictionary(n => n.ID));
        return store;
    }

    /// <summary>A character repository that only answers the wallet write-through the economy paths make.</summary>
    public class WalletSink : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? m, object?[]? args) =>
            m?.Name == nameof(ICharacterRepository.UpdateGoldAsync)
                ? Task.CompletedTask
                : throw new NotSupportedException(m?.Name);
    }

    /// <summary>
    /// A thread-safe bag that models Mongo's atomicity: the owner-filtered delete and the guarded $inc are
    /// serialized on one lock and reject when the row no longer holds what the caller expects, and every read
    /// hands back a CLONE so a handler's edits never leak into the store except through a write.
    /// </summary>
    internal sealed class AtomicBag : IInventoryRepository
    {
        private readonly object _lock = new();
        private readonly List<InventoryItem> _rows = new();
        private ulong _nextId;

        public AtomicBag(params InventoryItem[] rows)
        {
            _rows.AddRange(rows);
            _nextId = _rows.Count == 0 ? 0x1000UL : _rows.Max(r => r.Id) + 1;
        }

        private static InventoryItem Clone(InventoryItem s) => new InventoryItem
        {
            Id = s.Id, CharacterId = s.CharacterId, ItemId = s.ItemId, Count = s.Count, Slot = s.Slot,
            StorageType = s.StorageType,
        }.CopyInstanceStateFrom(s);

        public List<InventoryItem> Snapshot() { lock (_lock) return _rows.Select(Clone).ToList(); }

        public Task<IReadOnlyList<InventoryItem>> GetByCharacterAsync(uint c)
        {
            lock (_lock)
                return Task.FromResult<IReadOnlyList<InventoryItem>>(
                    _rows.Where(r => r.CharacterId == c).OrderBy(r => r.Slot).Select(Clone).ToList());
        }

        public Task<IReadOnlyList<InventoryItem>> GetEquipmentAsync(uint c)
        {
            lock (_lock)
                return Task.FromResult<IReadOnlyList<InventoryItem>>(
                    _rows.Where(r => r.CharacterId == c && r.StorageType == 1).Select(Clone).ToList());
        }

        public Task<InventoryItem?> GetByIdAsync(ulong id)
        {
            lock (_lock) { var r = _rows.FirstOrDefault(x => x.Id == id); return Task.FromResult(r is null ? null : Clone(r)); }
        }

        public Task AddItemAsync(InventoryItem item)
        {
            lock (_lock)
            {
                if (item.Id == 0) item.Id = _nextId++;
                _rows.Add(Clone(item));
                if (item.Id >= _nextId) _nextId = item.Id + 1;
            }
            return Task.CompletedTask;
        }

        public Task RemoveItemAsync(ulong id) { lock (_lock) _rows.RemoveAll(x => x.Id == id); return Task.CompletedTask; }

        public Task<bool> RemoveItemAsync(ulong id, uint c)
        {
            lock (_lock)
            {
                var r = _rows.FirstOrDefault(x => x.Id == id && x.CharacterId == c);
                if (r is null) return Task.FromResult(false);
                _rows.Remove(r);
                return Task.FromResult(true);
            }
        }

        public Task<int?> AdjustCountAsync(ulong id, uint c, int delta)
        {
            lock (_lock)
            {
                var r = _rows.FirstOrDefault(x => x.Id == id && x.CharacterId == c);
                if (r is null || (long)r.Count + delta < 0) return Task.FromResult<int?>(null);
                r.Count += delta;
                return Task.FromResult<int?>(r.Count);
            }
        }

        public Task UpdateItemAsync(InventoryItem item)
        {
            lock (_lock)
            {
                var r = _rows.FirstOrDefault(x => x.Id == item.Id);
                if (r is not null)
                {
                    r.CharacterId = item.CharacterId; r.Count = item.Count; r.Slot = item.Slot;
                    r.StorageType = item.StorageType; r.CopyInstanceStateFrom(item);
                }
            }
            return Task.CompletedTask;
        }

        // The atomic owner-flip the trade swap uses (E-H3): re-key the row in place under the lock, owner-filtered,
        // so a concurrent sell/deposit of the same uid races it to exactly one winner (models Mongo FindOneAndUpdate).
        public Task<bool> TryReassignAsync(ulong itemId, uint fromCharId, uint toCharId, byte toStorageType, short toSlot)
        {
            lock (_lock)
            {
                var r = _rows.FirstOrDefault(x => x.Id == itemId && x.CharacterId == fromCharId);
                if (r is null) return Task.FromResult(false);
                r.CharacterId = toCharId; r.StorageType = toStorageType; r.Slot = toSlot;
                return Task.FromResult(true);
            }
        }

        public Task<ulong> GetNextIdAsync() { lock (_lock) return Task.FromResult(_nextId++); }
        public Task<int> RepairSlotsAsync(uint c) => Task.FromResult(0);
    }

    private static (PlayerStateManager players, BroadcastService broadcast) Wire(Harness net,
        params PlayerState[] players)
    {
        var mgr = new PlayerStateManager(new NoDirtySink());
        foreach (var p in players) mgr.AddPlayer(p.SessionId, p);
        var broadcast = new BroadcastService(mgr, NullLogger<BroadcastService>.Instance);
        broadcast.SetServer(net.Server);
        return (mgr, broadcast);
    }

    private static IServiceProvider Services(IInventoryRepository inv, GameDataStore store, NpcStateManager? npcs = null)
    {
        var sc = new ServiceCollection()
            .AddScoped(_ => inv)
            .AddSingleton(store)
            .AddSingleton(DispatchProxy.Create<ICharacterRepository, WalletSink>());
        if (npcs is not null) sc.AddSingleton(npcs);
        return sc.BuildServiceProvider();
    }

    // A shop NPC at (5,5) on the seller's map so SellItemHandler's E-H6 proximity gate (CanTransact) passes; the
    // seller sits at the origin (well inside NpcInteractReach). Returns the handle the 1335 payload carries.
    private static NpcStateManager ShopNpcs(GameDataStore store, out uint handle)
    {
        var npcs = new NpcStateManager(store, NullLogger<NpcStateManager>.Instance);
        npcs.TrackSpawns(ShopMapId, [new SpawnEntry { MapId = ShopMapId, Index = 1, AssetIndex = ShopNpcAsset, PosX = 5, PosY = 5 }]);
        handle = npcs.AllocateHandle(ShopMapId, 1);
        return npcs;
    }

    private static byte[] Sell1335(uint npcHandle, ulong uid, byte qty)
    {
        var p = new byte[28];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(10), npcHandle); // shop NPC handle (E-H6)
        p[18] = 1;                                               // one entry
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(19), uid);
        p[27] = qty;
        return p;
    }

    private static byte[] Confirm1357()
    {
        var p = new byte[11];
        p[10] = 1;   // confirm flag; the handler skips 10 then reads it
        return p;
    }

    /// <summary>Set up a fresh live trade with A's side already carrying <paramref name="uid"/> (qty) and
    /// both sides locked and B confirmed, so A's confirm latches the swap.</summary>
    private static void ArmTradeAOffers(uint sa, uint sb, ulong uid, int qty)
    {
        TradeSessionService.Instance.Cancel(sa);
        TradeSessionService.Instance.Cancel(sb);
        var req = TradeSessionService.Instance.Request(sa, sb, HandleA, HandleB, _ => true);
        Assert.True(TradeSessionService.Instance.TryAccept(req));
        Assert.Equal(TradeOfferChange.Applied, req.AddItem(sa, uid, qty));
        req.SetLocked(sa, true);
        req.SetLocked(sb, true);
        Assert.False(req.ConfirmAndTryComplete(sb)); // B confirms; A's confirm (via the handler) completes it
    }

    // ---- 1. two concurrent ops on the same item: exactly one wins, item exists once, nothing minted ------

    [Fact]
    public async Task TradeAway_RacingSell_ExactlyOneWins_ItemExistsOnce_NoMint()
    {
        await using var net = await Harness.StartAsync(2);
        uint sa = net.Sessions[0].SessionId, sb = net.Sessions[1].SessionId;
        var a = new PlayerState { SessionId = sa, CharacterId = CharA, EntityId = HandleA, Name = "A" };
        var b = new PlayerState { SessionId = sb, CharacterId = CharB, EntityId = HandleB, Name = "B" };
        var (players, broadcast) = Wire(net, a, b);
        var store = PricedStore();

        for (int round = 0; round < 8; round++)
        {
            a.Gold = 0; b.Gold = 0;
            a.MapId = ShopMapId;
            var npcs = ShopNpcs(store, out var shop);
            ulong uid = 0x0E043DA800010000UL + (ulong)round;
            var inv = new AtomicBag(new InventoryItem
            {
                Id = uid, CharacterId = CharA, ItemId = SingleItem, Count = 1, StorageType = 0, Slot = 0,
            });
            var services = Services(inv, store, npcs);
            var sf = services.GetRequiredService<IServiceScopeFactory>();
            var trade = new TradeConfirmHandler(sf, NullLogger<TradeConfirmHandler>.Instance, players, broadcast);
            var sell = new SellItemHandler(sf, NullLogger<SellItemHandler>.Instance, players, broadcast);

            ArmTradeAOffers(sa, sb, uid, 1);

            await Task.WhenAll(
                Task.Run(() => trade.HandleAsync(net.Sessions[0], Confirm1357())),
                Task.Run(() => sell.HandleAsync(net.Sessions[0], Sell1335(shop, uid, 1))));

            var rows = inv.Snapshot();
            var xRows = rows.Where(r => r.Id == uid).ToList();
            Assert.True(xRows.Count <= 1, $"round {round}: item duplicated ({xRows.Count} copies)");

            bool tradeWon = xRows.Count == 1 && xRows[0].CharacterId == CharB;
            bool sellWon = xRows.Count == 0;
            Assert.True(tradeWon ^ sellWon, $"round {round}: exactly one path must take the item");

            if (tradeWon)
                Assert.Equal(0L, a.Gold);        // the seller was NOT paid: the item is the buyer's now
            else
                Assert.Equal(SinglePrice, (uint)a.Gold); // sold exactly once, not twice

            Assert.DoesNotContain(rows, r => r.CharacterId == CharB && r.Id != uid); // B got nothing extra
            TradeSessionService.Instance.Cancel(sa);
        }
    }

    // ---- 2. a partial-stack split racing another take of the same stack: no dupe, units conserved --------

    [Fact]
    public async Task PartialTradeOffer_RacingFullSell_NeverDupes_UnitsConserved()
    {
        await using var net = await Harness.StartAsync(2);
        uint sa = net.Sessions[0].SessionId, sb = net.Sessions[1].SessionId;
        var a = new PlayerState { SessionId = sa, CharacterId = CharA, EntityId = HandleA, Name = "A" };
        var b = new PlayerState { SessionId = sb, CharacterId = CharB, EntityId = HandleB, Name = "B" };
        var (players, broadcast) = Wire(net, a, b);
        var store = PricedStore();

        for (int round = 0; round < 8; round++)
        {
            a.Gold = 0; b.Gold = 0;
            a.MapId = ShopMapId;
            var npcs = ShopNpcs(store, out var shop);
            ulong uid = 0x0E043DA800020000UL + (ulong)round;
            var inv = new AtomicBag(new InventoryItem
            {
                Id = uid, CharacterId = CharA, ItemId = StackItem, Count = 10, StorageType = 0, Slot = 0,
            });
            var services = Services(inv, store, npcs);
            var sf = services.GetRequiredService<IServiceScopeFactory>();
            var trade = new TradeConfirmHandler(sf, NullLogger<TradeConfirmHandler>.Instance, players, broadcast);
            var sell = new SellItemHandler(sf, NullLogger<SellItemHandler>.Instance, players, broadcast);

            ArmTradeAOffers(sa, sb, uid, 5);   // offer 5 of the 10-stack

            await Task.WhenAll(
                Task.Run(() => trade.HandleAsync(net.Sessions[0], Confirm1357())),
                Task.Run(() => sell.HandleAsync(net.Sessions[0], Sell1335(shop, uid, 10))));  // sell the whole stack

            var rows = inv.Snapshot();
            int inBag = rows.Where(r => r.ItemId == StackItem).Sum(r => r.Count);
            int soldUnits = (int)(a.Gold / StackPrice);

            // Every one of the 10 units is accounted for exactly once: still in a bag, or sold. Never 11.
            Assert.Equal(10, inBag + soldUnits);
            // No stack row ever holds more than the 10 that existed (a dupe would show as an inflated stack).
            Assert.All(rows.Where(r => r.ItemId == StackItem), r => Assert.True(r.Count <= 10));
            // B never receives more than the 5 that could have been offered.
            Assert.True(rows.Where(r => r.CharacterId == CharB && r.ItemId == StackItem).Sum(r => r.Count) <= 5);
            TradeSessionService.Instance.Cancel(sa);
        }
    }

    // ---- 3. guild-vault deposit racing a sell of the same item -------------------------------------------

    [Fact]
    public async Task VaultDeposit_RacingSell_MovesTheItemOnce_NoMint()
    {
        await using var net = await Harness.StartAsync(1);
        uint sa = net.Sessions[0].SessionId;
        var a = new PlayerState { SessionId = sa, CharacterId = CharA, EntityId = HandleA, Name = "A" };
        var (players, broadcast) = Wire(net, a);
        var store = PricedStore();

        for (int round = 0; round < 8; round++)
        {
            a.Gold = 0;
            var (guild, refusal) = await GuildService.Instance.CreateAsync(CharA, "Depositor", 60, 6, $"Race {Guid.NewGuid():N}");
            Assert.Equal(GuildService.CreateRefusal.None, refusal);
            var g = Assert.IsType<Guild>(guild);
            try
            {
                a.MapId = ShopMapId;
                var npcs = ShopNpcs(store, out var shop);
                ulong uid = 0x0E043DA800030000UL + (ulong)round;
                var item = new InventoryItem { Id = uid, CharacterId = CharA, ItemId = SingleItem, Count = 1, StorageType = 0, Slot = 0 };
                var inv = new AtomicBag(item);
                var services = Services(inv, store, npcs);
                var sf = services.GetRequiredService<IServiceScopeFactory>();
                var sell = new SellItemHandler(sf, NullLogger<SellItemHandler>.Instance, players, broadcast);

                await Task.WhenAll(
                    Task.Run(() => GuildService.Instance.DepositAsync(g, item, inv, 100)),
                    Task.Run(() => sell.HandleAsync(net.Sessions[0], Sell1335(shop, uid, 1))));

                var rows = inv.Snapshot();
                bool inVault;
                lock (g.Gate) inVault = g.Storage.Any(s => s.Uid == uid);
                bool sold = a.Gold == SinglePrice;

                Assert.DoesNotContain(rows, r => r.Id == uid);   // never left in the bag
                Assert.True(inVault ^ sold, $"round {round}: the item must land in exactly one place");
                if (inVault) Assert.Equal(0L, a.Gold);            // not also paid for
            }
            finally { await GuildService.Instance.DisbandAsync(g); }
        }
    }

    // ---- 4. two concurrent sells of the same stack: sold once, credited once -----------------------------

    [Fact]
    public async Task DoubleSell_OfOneStack_SellsItOnce_CreditsOnce()
    {
        await using var net = await Harness.StartAsync(1);
        uint sa = net.Sessions[0].SessionId;
        var a = new PlayerState { SessionId = sa, CharacterId = CharA, EntityId = HandleA, Name = "A", Gold = 0 };
        var (players, broadcast) = Wire(net, a);
        a.MapId = ShopMapId;
        ulong uid = 0x0E043DA800040001UL;
        var inv = new AtomicBag(new InventoryItem { Id = uid, CharacterId = CharA, ItemId = SingleItem, Count = 1, StorageType = 0, Slot = 0 });
        var store = PricedStore();
        var npcs = ShopNpcs(store, out var shop);
        var services = Services(inv, store, npcs);
        var sf = services.GetRequiredService<IServiceScopeFactory>();
        var sell = new SellItemHandler(sf, NullLogger<SellItemHandler>.Instance, players, broadcast);

        await Task.WhenAll(
            Task.Run(() => sell.HandleAsync(net.Sessions[0], Sell1335(shop, uid, 1))),
            Task.Run(() => sell.HandleAsync(net.Sessions[0], Sell1335(shop, uid, 1))));

        Assert.DoesNotContain(inv.Snapshot(), r => r.Id == uid);   // removed exactly once
        Assert.Equal(SinglePrice, (uint)a.Gold);                   // credited once, not twice
    }

    // ---- 5. the single-session happy path: the whole stack moves, uid preserved, success notify sent -----

    [Fact]
    public async Task Trade_HappyPath_MovesWholeStack_KeepingItsUid_AndNotifiesBothSides()
    {
        await using var net = await Harness.StartAsync(2);
        uint sa = net.Sessions[0].SessionId, sb = net.Sessions[1].SessionId;
        var a = new PlayerState { SessionId = sa, CharacterId = CharA, EntityId = HandleA, Name = "A", Gold = 0 };
        var b = new PlayerState { SessionId = sb, CharacterId = CharB, EntityId = HandleB, Name = "B", Gold = 0 };
        var (players, broadcast) = Wire(net, a, b);
        ulong uid = 0x0E043DA800050001UL;
        var inv = new AtomicBag(new InventoryItem { Id = uid, CharacterId = CharA, ItemId = SingleItem, Count = 1, StorageType = 0, Slot = 0 });
        var services = Services(inv, PricedStore());
        var sf = services.GetRequiredService<IServiceScopeFactory>();
        var trade = new TradeConfirmHandler(sf, NullLogger<TradeConfirmHandler>.Instance, players, broadcast);

        ArmTradeAOffers(sa, sb, uid, 1);
        await trade.HandleAsync(net.Sessions[0], Confirm1357());

        var rows = inv.Snapshot();
        var x = Assert.Single(rows, r => r.Id == uid);             // the SAME uid, exactly one row (no dupe, no loss)
        Assert.Equal(CharB, x.CharacterId);                        // now the buyer's
        Assert.Equal(SingleItem, x.ItemId);
        Assert.Equal(1, x.Count);
        Assert.DoesNotContain(rows, r => r.CharacterId == CharA);  // gone from the seller

        // Both windows get the 2389 success notify — the success wire result is unchanged by the DB hardening.
        Assert.True(await ReceivesConfirmNotify(net.Readers[0]), "seller did not receive 2389");
        Assert.True(await ReceivesConfirmNotify(net.Readers[1]), "buyer did not receive 2389");
        TradeSessionService.Instance.Cancel(sa);
    }

    private static async Task<bool> ReceivesConfirmNotify(FrameReader reader)
    {
        // The handler sends 2388 acks then the 2389 OnConfirmTradePCNotify; read a few frames for the notify.
        for (int i = 0; i < 6; i++)
        {
            var (op, body) = await reader.NextAsync();
            if (op == ZoneS2COpcodes.OnConfirmTradePCNotify)
            {
                Assert.True(body.Length > 4);          // carries the wallet + inventory list, not an empty frame
                return true;
            }
        }
        return false;
    }

    // ---- integration: the real InventoryRepository owner-change is atomic against a real Mongo -----------

    private const string ConnectionString = "mongodb://localhost:27017";

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
    public async Task OwnerChange_RacingAnOwnerFilteredDelete_LeavesTheItemInExactlyOneBag()
    {
        if (!MongoUp()) return;

        var dbName = "ro2_test_econ_" + Guid.NewGuid().ToString("N");
        var client = new MongoClient(ConnectionString);
        try
        {
            var context = new MongoDbContext(ConnectionString, dbName);
            var repo = new InventoryRepository(context);
            var items = context.GetCollection<InventoryItem>("inventory_items");
            Assert.True(await repo.EnsureIndexesAsync());

            for (int round = 0; round < 20; round++)
            {
                ulong uid = 0x0E043DA8000A0000UL + (ulong)round;
                await items.DeleteManyAsync(_ => true);
                await items.InsertOneAsync(new InventoryItem { Id = uid, CharacterId = CharA, ItemId = SingleItem, Count = 1, StorageType = 0, Slot = 0 });

                // One task performs the trade owner-change (guarded remove from A, then insert for B at a free
                // cell); the other does a sell-style owner-filtered delete. Exactly one guarded remove wins.
                async Task<bool> OwnerChange()
                {
                    if (!await repo.RemoveItemAsync(uid, CharA)) return false;
                    await repo.AddItemAsync(new InventoryItem { Id = uid, CharacterId = CharB, ItemId = SingleItem, Count = 1, StorageType = 0, Slot = 0 });
                    return true;
                }
                var results = await Task.WhenAll(OwnerChange(), repo.RemoveItemAsync(uid, CharA));

                var live = await items.Find(i => i.Id == uid).ToListAsync();
                Assert.True(live.Count <= 1, $"round {round}: item duplicated");
                // The move won iff its guarded remove won; then the row is the buyer's, else it is gone.
                if (results[0]) { Assert.Single(live); Assert.Equal(CharB, live[0].CharacterId); }
                else Assert.Empty(live);
                Assert.True(results[0] ^ results[1]); // the two owner-filtered deletes cannot both have won
            }
        }
        finally { await client.DropDatabaseAsync(dbName); }
    }
}
