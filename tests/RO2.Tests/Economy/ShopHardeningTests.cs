using System.Buffers.Binary;
using System.Collections.Frozen;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Application;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Domain.GameData.Tables;
using RO2.Economy.Application;
using RO2.Economy.Domain;
using RO2.Inventory.Application;
using RO2.Inventory.Domain;
using RO2.Network.Protocol;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;
using static RO2.Tests.Packets.ReauditShared;

namespace RO2.Tests.Economy;

/// <summary>
/// Cluster S (NPC shops) hardening, on the shared <see cref="EconomyFixture"/> (fakes + invariants A = zeny
/// conservation, B = items, C = unique placement, D = no negative gold):
/// E-H1 buy proximity, E-H6 sell rules (bag only, Is_Sell, shop NPC in reach), E-H13/H14 fail-closed token buy and
/// bounded buy-back. A refused request must leave the wallet and every bag row exactly as they were.
/// </summary>
public sealed class ShopHardeningTests
{
    private const uint CharId = 664422u;
    private const uint MapId = 1100;
    private const uint ShopNpcAsset = 10030000;
    private const uint Stocked = 21110001;    // sold by the shop, Price_Buy 40, Is_Sell 1 / Price_Sell 10, Stack_Max 99
    private const uint NoSell = 21110004;     // Is_Sell 0 (Price_Sell 7)
    private const uint Token = 60000121;      // a token currency item
    private const long StartZeny = 1000;

    private static readonly ItemData[] Templates =
    {
        new() { ID = Stocked, Name = "Stocked", Price_Buy = 40, Price_Sell = 10, Is_Sell = 1, Stack_Max = 99 },
        new() { ID = NoSell, Name = "NoSell", Price_Buy = 50, Price_Sell = 7, Is_Sell = 0, Stack_Max = 99 },
        new() { ID = Token, Name = "Token", Price_Buy = 0, Price_Sell = 0, Is_Sell = 1, Stack_Max = 255 },
    };

    private static GameDataStore SeededStore()
    {
        var store = new GameDataStore();
        void Set(string prop, object value) => typeof(GameDataStore).GetProperty(prop)!.SetValue(store, value);
        Set(nameof(GameDataStore.Items), Templates.ToDictionary(t => t.ID).ToFrozenDictionary());
        Set(nameof(GameDataStore.Npcs), new[] { new NpcData { ID = ShopNpcAsset } }.ToDictionary(n => n.ID).ToFrozenDictionary());
        Set(nameof(GameDataStore.MerchantItems),
            new Dictionary<uint, uint[]> { [ShopNpcAsset] = new[] { Stocked } }.ToFrozenDictionary());
        typeof(GameDataStore).GetMethod("BuildNpcShopItems", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(store, null);
        return store;
    }

    /// <summary>Wraps the fixture's inventory and throws ONCE for a configured write (before applying it), so a test
    /// can fail exactly one repository call; the undo of a rolled-back buy then goes through.</summary>
    private sealed class FaultyInventory(FakeInventoryRepository inner) : IInventoryRepository
    {
        public HashSet<ulong> FailUpdateOnce { get; } = [];
        public HashSet<ulong> FailRemoveOnce { get; } = [];
        /// <summary>The N-th AddItemAsync call (1-based) throws once; 0 = never.</summary>
        public int FailAddNumber { get; set; }
        private int _adds;

        private static InvalidOperationException Injected(string what) => new($"injected {what} failure");

        public Task<IReadOnlyList<InventoryItem>> GetByCharacterAsync(uint characterId) => inner.GetByCharacterAsync(characterId);
        public Task<IReadOnlyList<InventoryItem>> GetEquipmentAsync(uint characterId) => inner.GetEquipmentAsync(characterId);
        public Task<InventoryItem?> GetByIdAsync(ulong id) => inner.GetByIdAsync(id);
        public Task AddItemAsync(InventoryItem item)
            => ++_adds == FailAddNumber ? throw Injected("add") : inner.AddItemAsync(item);
        public Task RemoveItemAsync(ulong itemId)
            => FailRemoveOnce.Remove(itemId) ? throw Injected("remove") : inner.RemoveItemAsync(itemId);
        public Task UpdateItemAsync(InventoryItem item)
            => FailUpdateOnce.Remove(item.Id) ? throw Injected("update") : inner.UpdateItemAsync(item);
        public Task<ulong> GetNextIdAsync() => inner.GetNextIdAsync();
        public Task<int> RepairSlotsAsync(uint characterId) => inner.RepairSlotsAsync(characterId);
    }

    private sealed class Rig : IAsyncDisposable
    {
        public required EconomyFixture Fx { get; init; }
        public required FaultyInventory Inventory { get; init; }
        public required PlayerState Player { get; init; }
        public required NpcStateManager Npcs { get; init; }
        public required GameDataStore Store { get; init; }
        public required Harness Net { get; init; }
        public required PlayerStateManager Players { get; init; }
        public required IServiceScopeFactory Scopes { get; init; }
        public required BroadcastService Broadcast { get; init; }

        public ValueTask DisposeAsync() => Net.DisposeAsync();

        public uint Spawn(float x, float y, uint map = MapId, int index = 1)
        {
            Npcs.TrackSpawns(map, [new SpawnEntry { MapId = map, Index = index, AssetIndex = ShopNpcAsset, PosX = x, PosY = y }]);
            return Npcs.AllocateHandle(map, index);
        }

        public async Task<(ushort Op, byte[] Body)> NextAsync() => await Net.Readers[0].NextAsync();
    }

    private static async Task<Rig> StartAsync()
    {
        var net = await Harness.StartAsync(1);
        var fx = new EconomyFixture();
        var store = SeededStore();
        var npcs = new NpcStateManager(store, NullLogger<NpcStateManager>.Instance);
        var player = fx.AddPlayer(CharId, StartZeny);
        player.SessionId = net.Sessions[0].SessionId;
        player.EntityId = 0x11u;
        player.MapId = MapId;
        var players = new PlayerStateManager(new NoDirtySink());
        players.AddPlayer(player.SessionId, player);
        var broadcast = new BroadcastService(players, NullLogger<BroadcastService>.Instance);
        broadcast.SetServer(net.Server);
        var inventory = new FaultyInventory(fx.Inventory);
        var services = new ServiceCollection()
            .AddSingleton<IInventoryRepository>(inventory)
            .AddSingleton(fx.CharacterRepo)
            .AddSingleton(store)
            .AddSingleton(npcs)
            .BuildServiceProvider();
        return new Rig
        {
            Fx = fx, Inventory = inventory, Player = player, Npcs = npcs, Store = store, Net = net, Players = players,
            Broadcast = broadcast, Scopes = services.GetRequiredService<IServiceScopeFactory>(),
        };
    }

    private static List<string> Shape(EconomyFixture fx)
        => fx.Inventory.Rows.OrderBy(r => r.Id)
            .Select(r => $"{r.Id}:{r.CharacterId}:{r.ItemId}:x{r.Count}:st{r.StorageType}:slot{r.Slot}").ToList();

    // ---------- requests ----------

    private static byte[] Buy1334(uint npcHandle, uint itemId, byte count = 1)
    {
        var p = new byte[27];
        BitConverter.GetBytes(npcHandle).CopyTo(p, 10);
        BitConverter.GetBytes(itemId).CopyTo(p, 20);
        p[24] = count;
        return p;
    }

    private static byte[] Sell1335(uint npcHandle, params (ulong Uid, byte Qty)[] entries)
    {
        var p = new byte[19 + 9 * entries.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(10), npcHandle);
        p[18] = (byte)entries.Length;
        for (int k = 0; k < entries.Length; k++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(19 + 9 * k), entries[k].Uid);
            p[19 + 9 * k + 8] = entries[k].Qty;
        }
        return p;
    }

    /// <summary>1336: [marker 1][len 16][u16 16][u16 msgTag][u32 handle][u64 uid].</summary>
    private static byte[] CancelSell1336(ulong uid)
    {
        var p = new byte[18];
        p[0] = 1;
        p[1] = 16;
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(2), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(4), (ushort)(ShopItemPackets.MsgTagCancelSellItem - 1));
        BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(10), uid);
        return p;
    }

    private static BuyItemHandler BuyHandler(Rig r)
        => new(r.Scopes, NullLogger<BuyItemHandler>.Instance, r.Players, r.Broadcast);

    private static SellItemHandler SellHandler(Rig r)
        => new(r.Scopes, NullLogger<SellItemHandler>.Instance, r.Players, r.Broadcast);

    private static CancelSellItemHandler CancelHandler(Rig r)
        => new(r.Scopes, NullLogger<CancelSellItemHandler>.Instance, r.Players, r.Broadcast);

    // ============================== E-H1: buy proximity ==============================

    private static async Task AssertBuyRefusedAsync(Rig rig, byte[] request, uint itemId)
    {
        var bagBefore = Shape(rig.Fx);
        var zenyBefore = rig.Player.Gold;
        await rig.Fx.AssertInvariantsAsync(async () =>
        {
            await BuyHandler(rig).HandleAsync(rig.Net.Sessions[0], request);
            var (op, body) = await rig.NextAsync();
            Assert.Equal(ZoneS2COpcodes.OnBuyItem, op);
            Assert.Equal(Hex(ShopItemPackets.BuildBuyRefusal2375(rig.Player.EntityId, 1, 0, itemId)), Hex(body));
        });
        Assert.Equal(zenyBefore, rig.Player.Gold);
        Assert.Equal(bagBefore, Shape(rig.Fx));
    }

    [Fact]
    public async Task Buy_FromAShopOnTheSameMapButFarAway_IsRefused_WalletAndBagUnchanged()
    {
        await using var rig = await StartAsync();
        rig.Player.X = 0; rig.Player.Y = 0;
        var far = rig.Spawn(x: 5000, y: 5000);

        await AssertBuyRefusedAsync(rig, Buy1334(far, Stocked, count: 3), Stocked);
    }

    [Fact]
    public async Task Buy_WhileDead_IsRefused()
    {
        await using var rig = await StartAsync();
        var shop = rig.Spawn(x: 5, y: 5);
        rig.Player.IsDead = true;

        await AssertBuyRefusedAsync(rig, Buy1334(shop, Stocked), Stocked);
    }

    [Fact]
    public async Task Buy_NearTheShop_Succeeds_ZenyDebitedExactlyOnce_ItemsDelivered()
    {
        await using var rig = await StartAsync();
        var shop = rig.Spawn(x: 10, y: 10);

        var before = rig.Fx.Snapshot();
        await BuyHandler(rig).HandleAsync(rig.Net.Sessions[0], Buy1334(shop, Stocked, count: 3));
        var (op, body) = await rig.NextAsync();
        var after = rig.Fx.Snapshot();

        Assert.Equal(ZoneS2COpcodes.OnBuyItem, op);
        Assert.NotEqual(Hex(ShopItemPackets.BuildBuyRefusal2375(rig.Player.EntityId, 1, 0, Stocked)), Hex(body));
        var created = after.Items.Select(i => i.Uid).Except(before.Items.Select(i => i.Uid)).ToHashSet();
        EconomyFixture.CheckAll(before, after, zenyDelta: -3 * 40, new ItemExpectation
        {
            CreatedUids = created,
            QuantityDelta = new Dictionary<uint, long> { [Stocked] = 3 },
        });
        Assert.Equal(StartZeny - 120, rig.Player.Gold);
    }

    // ============================== E-H13: fail-closed buy ==============================

    [Fact]
    public async Task Buy_WhenASecondStackCannotBeWritten_RefusesAndUndoesTheFirst_ZenyReturned()
    {
        await using var rig = await StartAsync();
        var shop = rig.Spawn(x: 5, y: 5);
        rig.Player.Gold = 10_000;          // 150 x 40 must be affordable: the refusal has to come from the failed write
        rig.Inventory.FailAddNumber = 2;   // 150 of a Stack_Max-99 item = stacks of 99 + 51; the second add throws

        await AssertBuyRefusedAsync(rig, Buy1334(shop, Stocked, count: 150), Stocked);
        Assert.Empty(rig.Fx.Inventory.Rows);
    }

    // The write phase itself, with a token cost. Bag: token stacks of 5 (slot 0) and 3 (slot 1); a buy of 1 Stocked
    // costs 6 tokens: slot 0 is emptied (left 0), slot 1 is left at 2.

    private static ShopInventoryAdapter Adapter(Rig rig) => new(rig.Inventory, rig.Store.Items, NullLogger.Instance);

    private static async Task<(IShopBag Bag, IReadOnlyList<StackTake> Takes, ShopBuyPlan Plan)> PlanAsync(
        Rig rig, int quantity = 1)
    {
        var bag = await Adapter(rig).ReadBagAsync(CharId);
        var cost = BuyItemHandler.TokenCostOf(new BuyTokenInfo { ID = 1, TokenItemID1 = Token, NeedValue1 = 6 });
        var takes = ShopBuyPlanner.PlanTokenTake(bag.Stacks, cost, quantity);
        Assert.NotNull(takes);
        var plan = ShopBuyPlanner.Plan(bag.Stacks, bag.FreeCells, Stocked, quantity, 99);
        Assert.NotNull(plan);
        return (bag, takes!, plan!);
    }

    private static Task<ShopBuyWrite> WriteAsync(Rig rig, IShopBag bag, ShopBuyPlan plan,
        IReadOnlyList<StackTake> takes, int quantity = 1)
        => Adapter(rig).WriteBuyAsync(bag, CharId, Stocked, quantity, plan, templateDurability: 0, takes);

    [Fact]
    public async Task TokenBuy_Success_DeliversAndTakesTokens_AllInvariantsHold()
    {
        await using var rig = await StartAsync();
        var emptied = rig.Fx.GiveItem(CharId, Token, 5, slot: 0);
        rig.Fx.GiveItem(CharId, Token, 3, slot: 1);
        var (bag, takes, plan) = await PlanAsync(rig);

        var before = rig.Fx.Snapshot();
        var written = await WriteAsync(rig, bag, plan, takes);
        var after = rig.Fx.Snapshot();

        var created = after.Items.Select(i => i.Uid).Except(before.Items.Select(i => i.Uid)).ToHashSet();
        EconomyFixture.CheckAll(before, after, zenyDelta: 0, new ItemExpectation
        {
            ConsumedUids = new HashSet<ulong> { emptied.Id },
            CreatedUids = created,
            QuantityDelta = new Dictionary<uint, long> { [Stocked] = 1, [Token] = -6 },
        });
        Assert.Single(written.Stacks);
        Assert.Equal(2, written.TokenTouches.Count);
        Assert.Equal(0, BuyItemHandler.TokenRecordOf(written.TokenTouches[0], CharId)[14]);   // the emptied stack's 2379 record carries count 0
    }

    [Theory]
    [InlineData("update")]   // the second token stack cannot be shrunk, after the first was emptied
    [InlineData("remove")]   // the first token stack cannot be emptied, after the bought stack was stored
    public async Task TokenBuy_WhenATokenWriteThrows_NothingIsDelivered_NothingTaken(string failing)
    {
        await using var rig = await StartAsync();
        var first = rig.Fx.GiveItem(CharId, Token, 5, slot: 0);
        var second = rig.Fx.GiveItem(CharId, Token, 3, slot: 1);
        var (bag, takes, plan) = await PlanAsync(rig);
        if (failing == "update") rig.Inventory.FailUpdateOnce.Add(second.Id);
        else rig.Inventory.FailRemoveOnce.Add(first.Id);
        var shapeBefore = Shape(rig.Fx);

        await rig.Fx.AssertInvariantsAsync(async () =>
            await Assert.ThrowsAsync<InvalidOperationException>(() => WriteAsync(rig, bag, plan, takes)));

        Assert.Equal(shapeBefore, Shape(rig.Fx));   // the bought stack is gone, both token stacks are back as they were
    }

    [Fact]
    public async Task TokenBuy_Restack_WhenATokenWriteThrows_TheStackIsLoweredBack()
    {
        await using var rig = await StartAsync();
        rig.Fx.GiveItem(CharId, Token, 5, slot: 0);
        var second = rig.Fx.GiveItem(CharId, Token, 3, slot: 1);
        rig.Fx.GiveItem(CharId, Stocked, 5, slot: 2);   // the buy restacks onto this one (5 + 1 <= 99)
        var (bag, takes, plan) = await PlanAsync(rig);
        Assert.NotNull(plan.RestackId);
        rig.Inventory.FailUpdateOnce.Add(second.Id);
        var shapeBefore = Shape(rig.Fx);

        await rig.Fx.AssertInvariantsAsync(async () =>
            await Assert.ThrowsAsync<InvalidOperationException>(() => WriteAsync(rig, bag, plan, takes)));

        Assert.Equal(shapeBefore, Shape(rig.Fx));
    }

    // ============================== E-H6: sell rules ==============================

    private async Task AssertSellRefusedAsync(Rig rig, byte[] request)
    {
        var bagBefore = Shape(rig.Fx);
        await rig.Fx.AssertInvariantsAsync(async () =>
        {
            await SellHandler(rig).HandleAsync(rig.Net.Sessions[0], request);
            var (op, body) = await rig.NextAsync();
            Assert.Equal(ZoneS2COpcodes.OnSellItem, op);
            Assert.Equal(Hex(ShopItemPackets.BuildSellItem2376(rig.Player.EntityId, ShopItemPackets.SellResultNothingSold,
                rig.Player.Gold, BuyBackLedger.UidsOf(rig.Player), Array.Empty<byte[]>())), Hex(body));
        });
        Assert.Equal(StartZeny, rig.Player.Gold);
        Assert.Equal(bagBefore, Shape(rig.Fx));
        Assert.Empty(BuyBackLedger.UidsOf(rig.Player));   // nothing went on the buy-back list
    }

    [Theory]
    [InlineData(1)]   // worn
    [InlineData(2)]   // bank
    public async Task Sell_AnItemNotInTheBag_IsRefused_Result1(byte storageType)
    {
        await using var rig = await StartAsync();
        var shop = rig.Spawn(x: 5, y: 5);
        var item = rig.Fx.GiveItem(CharId, Stocked, 1, storageType: storageType, slot: 3);

        await AssertSellRefusedAsync(rig, Sell1335(shop, (item.Id, 1)));
    }

    [Fact]
    public async Task Sell_AnItemWithIsSell0_IsRefused_Result1()
    {
        await using var rig = await StartAsync();
        var shop = rig.Spawn(x: 5, y: 5);
        var item = rig.Fx.GiveItem(CharId, NoSell, 4, slot: 0);

        await AssertSellRefusedAsync(rig, Sell1335(shop, (item.Id, 4)));
    }

    [Fact]
    public async Task Sell_ToAFarShopNpc_IsRefused()
    {
        await using var rig = await StartAsync();
        var far = rig.Spawn(x: 5000, y: 5000);
        var item = rig.Fx.GiveItem(CharId, Stocked, 2, slot: 0);

        await AssertSellRefusedAsync(rig, Sell1335(far, (item.Id, 2)));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(0x00ABCD13u)]
    public async Task Sell_ToAnUnresolvedNpcHandle_IsRefused(uint handle)
    {
        await using var rig = await StartAsync();
        var item = rig.Fx.GiveItem(CharId, Stocked, 2, slot: 0);

        await AssertSellRefusedAsync(rig, Sell1335(handle, (item.Id, 2)));
    }

    [Fact]
    public async Task Sell_ToANpcOnAnotherMap_IsRefused()
    {
        await using var rig = await StartAsync();
        var other = rig.Spawn(x: 0, y: 0, map: MapId + 1, index: 4);
        var item = rig.Fx.GiveItem(CharId, Stocked, 2, slot: 0);

        await AssertSellRefusedAsync(rig, Sell1335(other, (item.Id, 2)));
    }

    [Fact]
    public async Task Sell_ABagItemNearTheShop_Succeeds_PaidPriceSellOnce()
    {
        await using var rig = await StartAsync();
        var shop = rig.Spawn(x: 10, y: 10);
        var item = rig.Fx.GiveItem(CharId, Stocked, 4, slot: 0);

        var before = rig.Fx.Snapshot();
        await SellHandler(rig).HandleAsync(rig.Net.Sessions[0], Sell1335(shop, (item.Id, 4)));
        var (op, _) = await rig.NextAsync();
        var after = rig.Fx.Snapshot();

        Assert.Equal(ZoneS2COpcodes.OnSellItem, op);
        EconomyFixture.CheckAll(before, after, zenyDelta: 4 * 10, new ItemExpectation
        {
            ConsumedUids = new HashSet<ulong> { item.Id },
            QuantityDelta = new Dictionary<uint, long> { [Stocked] = -4 },
        });
        Assert.Equal(StartZeny + 40, rig.Player.Gold);
    }

    [Fact]
    public async Task Sell_MixedRequest_SellsOnlyTheSellableBagStack()
    {
        await using var rig = await StartAsync();
        var shop = rig.Spawn(x: 10, y: 10);
        var blocked = rig.Fx.GiveItem(CharId, NoSell, 3, slot: 0);
        var worn = rig.Fx.GiveItem(CharId, Stocked, 1, storageType: 1, slot: 4);
        var ok = rig.Fx.GiveItem(CharId, Stocked, 2, slot: 1);

        var before = rig.Fx.Snapshot();
        await SellHandler(rig).HandleAsync(rig.Net.Sessions[0], Sell1335(shop, (blocked.Id, 3), (worn.Id, 1), (ok.Id, 2)));
        await rig.NextAsync();
        var after = rig.Fx.Snapshot();

        EconomyFixture.CheckAll(before, after, zenyDelta: 2 * 10, new ItemExpectation
        {
            ConsumedUids = new HashSet<ulong> { ok.Id },
            QuantityDelta = new Dictionary<uint, long> { [Stocked] = -2 },
        });
        Assert.Contains(rig.Fx.Inventory.Rows, r => r.Id == blocked.Id && r.Count == 3);
        Assert.Contains(rig.Fx.Inventory.Rows, r => r.Id == worn.Id && r.StorageType == 1);
    }

    // ============================== E-H14: bounded buy-back ==============================

    private static InventoryItem Sold(ulong uid) => new() { Id = uid, CharacterId = CharId, ItemId = Stocked, Count = 2 };

    [Fact]
    public async Task BuyBack_WithAFullBag_IsRefused_20020_NothingChargedNothingGiven_EntryKept()
    {
        await using var rig = await StartAsync();
        for (short slot = 0; slot < BagCapacity.MainBagCells; slot++) rig.Fx.GiveItem(CharId, NoSell, 1, slot: slot);
        BuyBackLedger.RecordSale(rig.Player, Sold(900), soldQty: 2, paid: 20);
        var bagBefore = Shape(rig.Fx);

        await rig.Fx.AssertInvariantsAsync(async () =>
        {
            await CancelHandler(rig).HandleAsync(rig.Net.Sessions[0], CancelSell1336(900));
            var (op, body) = await rig.NextAsync();
            Assert.Equal(ZoneS2COpcodes.OnCancelSellItem, op);
            Assert.Equal(Hex(CancelSellItemHandler.Build2377(rig.Player.EntityId, 20020, StartZeny, null, 900)), Hex(body));
        });

        Assert.Equal(StartZeny, rig.Player.Gold);
        Assert.Equal(bagBefore, Shape(rig.Fx));
        Assert.Equal(new ulong[] { 900 }, BuyBackLedger.UidsOf(rig.Player));   // still on the list: a later buy-back can work
    }

    [Fact]
    public async Task BuyBack_WithOneFreeCell_LandsInThatCell_Debited()
    {
        await using var rig = await StartAsync();
        for (short slot = 0; slot < BagCapacity.MainBagCells; slot++)
            if (slot != 7) rig.Fx.GiveItem(CharId, NoSell, 1, slot: slot);
        BuyBackLedger.RecordSale(rig.Player, Sold(900), soldQty: 2, paid: 20);

        var before = rig.Fx.Snapshot();
        await CancelHandler(rig).HandleAsync(rig.Net.Sessions[0], CancelSell1336(900));
        var (op, _) = await rig.NextAsync();
        var after = rig.Fx.Snapshot();

        Assert.Equal(ZoneS2COpcodes.OnCancelSellItem, op);
        var created = after.Items.Select(i => i.Uid).Except(before.Items.Select(i => i.Uid)).ToHashSet();
        EconomyFixture.CheckAll(before, after, zenyDelta: -20, new ItemExpectation
        {
            CreatedUids = created,
            QuantityDelta = new Dictionary<uint, long> { [Stocked] = 2 },
        });
        var back = Assert.Single(rig.Fx.Inventory.Rows, r => r.ItemId == Stocked);
        Assert.Equal((short)7, back.Slot);
        Assert.Equal(StartZeny - 20, rig.Player.Gold);
    }

    // ============================== the flows behind the handlers (economy application service) ==============================
    // The frames and stored state are spelled out from the pre-extraction handlers' own rules, not read back from the
    // code under test.

    private static InventoryItem StoredRow(Rig rig, ulong id) => rig.Fx.Inventory.Rows.Single(r => r.Id == id);

    private static long StoredGold(Rig rig) => rig.Fx.Characters.Rows.Single(c => c.Id == CharId).Gold;

    private static byte[] WithCount(InventoryItem row, byte count)
    {
        var record = WorldEntryService.BuildItemRecord126(row);
        record[14] = count;
        return record;
    }

    [Fact]
    public async Task Buy_Restack_TheRecordCarriesTheExistingUidWithTheNewTotal_AndTheWalletIsWrittenThrough()
    {
        await using var rig = await StartAsync();
        var shop = rig.Spawn(x: 10, y: 10);
        var existing = rig.Fx.GiveItem(CharId, Stocked, 5, slot: 2);

        await BuyHandler(rig).HandleAsync(rig.Net.Sessions[0], Buy1334(shop, Stocked, count: 3));
        var (op, body) = await rig.NextAsync();

        Assert.Equal(ZoneS2COpcodes.OnBuyItem, op);
        var live = StoredRow(rig, existing.Id);
        Assert.Equal(8, live.Count);   // the live row was mutated, no second row appeared
        Assert.Single(rig.Fx.Inventory.Rows, r => r.ItemId == Stocked);
        Assert.Equal(Hex(ShopItemPackets.BuildBuyItem2375(rig.Player.EntityId, StartZeny - 120, Stocked,
            new List<byte[]> { WorldEntryService.BuildItemRecord126(live) })), Hex(body));
        Assert.Equal(existing.Id, BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(body.Length - 126)));
        Assert.Equal(StartZeny - 120, StoredGold(rig));   // persisted, not only in the live wallet
    }

    [Theory]
    [InlineData(100_000u, short.MaxValue)]   // a template durability past the i16 range is capped
    [InlineData(50u, (short)50)]
    public async Task WriteBuy_ANewStackCarriesTheCappedTemplateDurability_AndAbilityValue0(uint templateDurability, short expected)
    {
        await using var rig = await StartAsync();
        var bag = await Adapter(rig).ReadBagAsync(CharId);
        var plan = ShopBuyPlanner.Plan(bag.Stacks, bag.FreeCells, Stocked, 1, 99);

        var written = await Adapter(rig).WriteBuyAsync(bag, CharId, Stocked, 1, plan!, templateDurability, tokenTakes: null);

        var row = Assert.Single(rig.Fx.Inventory.Rows);
        Assert.Equal((expected, 0u), (row.Durability, row.AbilityValue));   // 0, not null: not the template's Abili_val
        var snapshot = Assert.Single(written.Stacks);
        Assert.Equal((row.Id, expected, 0u), (snapshot.Id, snapshot.Durability, snapshot.AbilityValue));
    }

    [Fact]
    public async Task Sell_Frame_ListsTheUidsInRequestOrder_FullySoldRecordCount0_PartialIsWhatIsLeft_WalletWrittenThrough()
    {
        await using var rig = await StartAsync();
        var shop = rig.Spawn(x: 10, y: 10);
        var whole = rig.Fx.GiveItem(CharId, Stocked, 4, slot: 0);
        var part = rig.Fx.GiveItem(CharId, Stocked, 10, slot: 1);

        await SellHandler(rig).HandleAsync(rig.Net.Sessions[0], Sell1335(shop, (whole.Id, 4), (part.Id, 3)));
        var (op, body) = await rig.NextAsync();

        Assert.Equal(ZoneS2COpcodes.OnSellItem, op);
        // The records are the PRE-sale rows (the clones GiveItem returned) with the count byte forced.
        var expected = ShopItemPackets.BuildSellItem2376(rig.Player.EntityId, ShopCodes.SellResultSold, StartZeny + 70,
            new[] { whole.Id, part.Id }, new List<byte[]> { WithCount(whole, 0), WithCount(part, 7) });
        Assert.Equal(Hex(expected), Hex(body));
        Assert.DoesNotContain(rig.Fx.Inventory.Rows, r => r.Id == whole.Id);
        Assert.Equal(7, StoredRow(rig, part.Id).Count);
        Assert.Equal(StartZeny + 70, StoredGold(rig));
        Assert.Equal(new[] { whole.Id, part.Id }, BuyBackLedger.UidsOf(rig.Player));
    }

    [Theory]
    [InlineData(0, (byte)0)]
    [InlineData(-3, (byte)0)]
    [InlineData(1, (byte)1)]
    [InlineData(255, (byte)255)]
    [InlineData(300, (byte)255)]
    public void Sell_TheRecordCountByte_IsZeroWhenFullySold_ElseClampedTo255(int newCount, byte expected)
        => Assert.Equal(expected, SellItemHandler.RecordCount(newCount));

    [Fact]
    public async Task Sell_AStackOver255_LeavesTheRecordCountSaturatedAt255()
    {
        await using var rig = await StartAsync();
        var shop = rig.Spawn(x: 10, y: 10);
        var big = rig.Fx.GiveItem(CharId, Stocked, 300, slot: 0);

        await SellHandler(rig).HandleAsync(rig.Net.Sessions[0], Sell1335(shop, (big.Id, 1)));
        var (_, body) = await rig.NextAsync();

        Assert.Equal(255, body[body.Length - 126 + 14]);
        Assert.Equal(299, StoredRow(rig, big.Id).Count);
    }

    [Fact]
    public void BuyBackList_AnUnseenPlayerAndAnEmptiedLedger_EncodeTheSame2376()
    {
        var unseen = new PlayerState { EntityId = 0x11u, Gold = 5 };
        var emptied = new PlayerState { EntityId = 0x11u, Gold = 5 };
        BuyBackLedger.RecordSale(emptied, Sold(900), soldQty: 2, paid: 20);
        Assert.True(BuyBackLedger.TryTake(emptied, 900, out _, out _));

        byte[] Frame(PlayerState p) => ShopItemPackets.BuildSellItem2376(p.EntityId, ShopItemPackets.SellResultNothingSold,
            p.Gold, BuyBackLedger.UidsOf(p), Array.Empty<byte[]>());

        Assert.Empty(BuyBackLedger.UidsOf(unseen));
        Assert.Empty(BuyBackLedger.UidsOf(emptied));
        Assert.Equal(Hex(Frame(unseen)), Hex(Frame(emptied)));
    }

    [Fact]
    public async Task BuyBack_Success_ReturnsTheSoldQuantityInTheFirstFreeCell_AndWritesTheWalletThrough()
    {
        await using var rig = await StartAsync();
        BuyBackLedger.RecordSale(rig.Player, Sold(900), soldQty: 2, paid: 20);

        await CancelHandler(rig).HandleAsync(rig.Net.Sessions[0], CancelSell1336(900));
        var (op, body) = await rig.NextAsync();

        Assert.Equal(ZoneS2COpcodes.OnCancelSellItem, op);
        var back = Assert.Single(rig.Fx.Inventory.Rows);
        Assert.Equal((Stocked, 2, (short)0), (back.ItemId, back.Count, back.Slot));
        Assert.Equal(Hex(CancelSellItemHandler.Build2377(rig.Player.EntityId, 0, StartZeny - 20,
            WorldEntryService.BuildItemRecord126(back), 900)), Hex(body));
        Assert.Equal(StartZeny - 20, StoredGold(rig));
        Assert.Empty(BuyBackLedger.UidsOf(rig.Player));   // a uid is bought back once
    }

    [Fact]
    public async Task BuyBack_WhenTheUidWasReusedMeanwhile_TheItemGetsANewUid_AndTheFrameStillEchoesTheRequestedOne()
    {
        await using var rig = await StartAsync();
        rig.Fx.Inventory.Seed(new InventoryItem { Id = 900, CharacterId = CharId, ItemId = NoSell, Count = 1, Slot = 0 });
        BuyBackLedger.RecordSale(rig.Player, Sold(900), soldQty: 2, paid: 20);

        await CancelHandler(rig).HandleAsync(rig.Net.Sessions[0], CancelSell1336(900));
        var (_, body) = await rig.NextAsync();

        var back = Assert.Single(rig.Fx.Inventory.Rows, r => r.ItemId == Stocked);
        Assert.NotEqual(900UL, back.Id);
        Assert.Contains(rig.Fx.Inventory.Rows, r => r.Id == 900 && r.ItemId == NoSell && r.Count == 1);   // the other stack is untouched
        Assert.Equal(Hex(CancelSellItemHandler.Build2377(rig.Player.EntityId, 0, StartZeny - 20,
            WorldEntryService.BuildItemRecord126(back), 900)), Hex(body));
        Assert.Equal(900UL, BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(3 + 144)));   // +144 echoes the request
        Assert.Equal(back.Id, BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(3 + 18)));  // the record carries the new uid
    }

    [Fact]
    public async Task TakeForSale_HoldsTheInventoryGateOnceAcrossTheWholeLoop_NotPerItem()
    {
        var fx = new EconomyFixture();
        var first = fx.GiveItem(CharId, Stocked, 4, slot: 0);
        var second = fx.GiveItem(CharId, Stocked, 2, slot: 1);
        var adapter = new ShopInventoryAdapter(fx.InventoryRepo, new Dictionary<uint, ItemData>(), NullLogger.Instance);
        var events = new List<string>();
        Task? competitor = null;

        await adapter.TakeForSaleAsync(CharId, [new ShopSellEntry(first.Id, 4), new ShopSellEntry(second.Id, 2)], _ => true, line =>
        {
            events.Add($"sold:{line.Row.Id}");
            // Another mutation of the same character (a trade-away, a mail attach) queues behind the gate the moment the
            // first stack is taken; with the gate held per item it would run between the two stacks.
            competitor ??= CharacterInventoryGate.RunAsync(CharId, () => { events.Add("other"); return Task.FromResult(0); });
        });
        await competitor!;

        Assert.Equal([$"sold:{first.Id}", $"sold:{second.Id}", "other"], events);
    }
}
