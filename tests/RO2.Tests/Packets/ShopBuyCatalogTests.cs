using System.Collections.Frozen;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Network.Protocol;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;
using static RO2.Tests.Packets.ReauditShared;

namespace RO2.Tests.Packets;

/// <summary>A 1334 ReqBuyItem must name a live NPC on the player's map whose shop stocks the item. Items outside that NPC's catalog are refused. A refusal answers with the failed-buy 2375 and leaves the bag and wallet unchanged. A valid purchase is debited through TryDebit and delivers the stack.</summary>
public sealed class ShopBuyCatalogTests
{
    private const uint CharId = 664422u;
    private const uint MapId = 1100;
    private const uint ShopNpcAsset = 10030000;     
    private const uint FieldNpcAsset = 11130000;    
    private const uint BareNpcAsset = 12000000;     
    private const uint Stocked = 21110001;          
    private const uint OffCatalog = 21110002;       
    private const uint FreeItem = 21110003;         
    private const long StartZeny = 1000;

    private static readonly ItemData[] Templates =
    {
        new() { ID = Stocked, Name = "Stocked", Price_Buy = 40, Stack_Max = 99 },
        new() { ID = OffCatalog, Name = "OffCatalog", Price_Buy = 500, Stack_Max = 99 },
        new() { ID = FreeItem, Name = "FreeItem", Price_Buy = 0, Stack_Max = 99 },
    };

    private static GameDataStore SeededStore()
    {
        var store = new GameDataStore();
        void Set(string prop, object value) =>
            typeof(GameDataStore).GetProperty(prop)!.SetValue(store, value);
        Set(nameof(GameDataStore.Items), Templates.ToDictionary(t => t.ID).ToFrozenDictionary());
        Set(nameof(GameDataStore.Npcs), new[]
        {
            new NpcData { ID = ShopNpcAsset },
            new NpcData { ID = FieldNpcAsset, Function_Type_1 = 11, Function_ID_1 = ShopNpcAsset },
            new NpcData { ID = BareNpcAsset },
        }.ToDictionary(n => n.ID).ToFrozenDictionary());
        Set(nameof(GameDataStore.MerchantItems),
            new Dictionary<uint, uint[]> { [ShopNpcAsset] = new[] { Stocked } }.ToFrozenDictionary());
        typeof(GameDataStore).GetMethod("BuildNpcShopItems", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(store, null);
        return store;
    }

    public class WalletSink : DispatchProxy
    {
        public static long Writes;
        protected override object? Invoke(MethodInfo? m, object?[]? args)
        {
            if (m?.Name != nameof(ICharacterRepository.UpdateGoldAsync)) throw new NotSupportedException(m?.Name);
            Interlocked.Increment(ref Writes);
            return Task.CompletedTask;
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        public required Harness Net { get; init; }
        public required BuyItemHandler Handler { get; init; }
        public required PlayerState Player { get; init; }
        public required NpcStateManager Npcs { get; init; }
        public required GearInventory Bag { get; init; }
        public required GameDataStore Store { get; init; }

        public ValueTask DisposeAsync() => Net.DisposeAsync();

 /// <summary>The handle of a live NPC of <paramref name="asset"/> on <paramref name="map"/>.</summary>
        public uint Spawn(uint asset, uint map, int index)
        {
            var spawn = new SpawnEntry { MapId = map, Index = index, AssetIndex = asset };
            Npcs.TrackSpawns(map, [spawn]);
            return Npcs.AllocateHandle(map, index);
        }
    }

    private static async Task<Rig> StartAsync()
    {
        var net = await Harness.StartAsync(1);
        var store = SeededStore();
        var npcs = new NpcStateManager(store, NullLogger<NpcStateManager>.Instance);
        var players = new PlayerStateManager(new NoDirtySink());
        var player = new PlayerState
        {
            SessionId = net.Sessions[0].SessionId, CharacterId = CharId, Name = "Buyer", EntityId = 0x11u,
            MapId = MapId, Gold = StartZeny,
        };
        players.AddPlayer(player.SessionId, player);
        var broadcast = new BroadcastService(players, NullLogger<BroadcastService>.Instance);
        broadcast.SetServer(net.Server);
        var bag = new GearInventory([]);
        var services = new ServiceCollection()
            .AddScoped<IInventoryRepository>(_ => bag)
            .AddSingleton(store)
            .AddSingleton(npcs)
            .AddSingleton(DispatchProxy.Create<ICharacterRepository, WalletSink>())
            .BuildServiceProvider();
        var handler = new BuyItemHandler(services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<BuyItemHandler>.Instance, players, broadcast);
        return new Rig { Net = net, Handler = handler, Player = player, Npcs = npcs, Bag = bag, Store = store };
    }

 /// <summary>A 1334 body: NPC handle @10, Merchant row @18, item @20, count @24, token row @25.</summary>
    private static byte[] Buy1334(uint npcHandle, uint itemId, byte count = 1, ushort merchantRow = 0)
    {
        var p = new byte[27];
        BitConverter.GetBytes(npcHandle).CopyTo(p, 10);
        BitConverter.GetBytes(merchantRow).CopyTo(p, 18);
        BitConverter.GetBytes(itemId).CopyTo(p, 20);
        p[24] = count;
        return p;
    }

    private async Task AssertRefusedAsync(Rig rig, byte[] request, uint itemId)
    {
        var before = Interlocked.Read(ref WalletSink.Writes);
        await rig.Handler.HandleAsync(rig.Net.Sessions[0], request);

        var (op, body) = await rig.Net.Readers[0].NextAsync();
        Assert.Equal(ZoneS2COpcodes.OnBuyItem, op);
        Assert.Equal(Hex(ShopItemPackets.BuildBuyRefusal2375(rig.Player.EntityId, 1, 0, itemId)), Hex(body));
        Assert.Empty(rig.Bag.Items);                        
        Assert.Equal(StartZeny, rig.Player.Gold);           
        Assert.Equal(before, Interlocked.Read(ref WalletSink.Writes));   
    }

    
    [Theory]
    [InlineData(Stocked)]       
    [InlineData(OffCatalog)]    
    [InlineData(FreeItem)]      
    public async Task ForgedNpcHandle_IsRefused_NoItemNoZenyChange(uint itemId)
    {
        await using var rig = await StartAsync();
        await AssertRefusedAsync(rig, Buy1334(0x00ABCD13, itemId), itemId);   
    }

    [Fact]
    public async Task HandleZero_IsRefused()
    {
        await using var rig = await StartAsync();
        await AssertRefusedAsync(rig, Buy1334(0, FreeItem), FreeItem);
    }

    [Theory]
    [InlineData(OffCatalog)]
    [InlineData(FreeItem)]
    public async Task ItemOutsideTheMerchantsCatalog_IsRefused_FromAValidMerchant(uint itemId)
    {
        await using var rig = await StartAsync();
        var shop = rig.Spawn(ShopNpcAsset, MapId, 1);
        await AssertRefusedAsync(rig, Buy1334(shop, itemId), itemId);
    }

    [Fact]
    public async Task FieldNpcThatOpensTheShop_CannotSellWhatTheShopDoesNot()
    {
        await using var rig = await StartAsync();
        var field = rig.Spawn(FieldNpcAsset, MapId, 2);
        await AssertRefusedAsync(rig, Buy1334(field, FreeItem), FreeItem);
    }

    [Fact]
    public async Task NpcWithNoShop_IsRefused()
    {
        await using var rig = await StartAsync();
        var bare = rig.Spawn(BareNpcAsset, MapId, 3);
        await AssertRefusedAsync(rig, Buy1334(bare, Stocked), Stocked);
    }

    [Fact]
    public async Task ShopOnAnotherMap_IsRefused()
    {
        await using var rig = await StartAsync();
        var far = rig.Spawn(ShopNpcAsset, MapId + 1, 4);   
        await AssertRefusedAsync(rig, Buy1334(far, Stocked), Stocked);
    }

    [Fact]
    public async Task DeadShopNpc_IsRefused()
    {
        await using var rig = await StartAsync();
        var shop = rig.Spawn(ShopNpcAsset, MapId, 5);
        rig.Npcs.GetNpc(shop)!.IsDead = true;
        await AssertRefusedAsync(rig, Buy1334(shop, Stocked), Stocked);
    }

    
    [Theory]
    [InlineData(ShopNpcAsset)]    
    [InlineData(FieldNpcAsset)]   
    public async Task StockedItem_FromAValidMerchant_Succeeds_AndDebitsThePriceFromData(uint asset)
    {
        await using var rig = await StartAsync();
        var shop = rig.Spawn(asset, MapId, 1);

        
        await rig.Handler.HandleAsync(rig.Net.Sessions[0], Buy1334(shop, Stocked, count: 3));

        var (op, body) = await rig.Net.Readers[0].NextAsync();
        Assert.Equal(ZoneS2COpcodes.OnBuyItem, op);
        Assert.NotEqual(Hex(ShopItemPackets.BuildBuyRefusal2375(rig.Player.EntityId, 1, 0, Stocked)), Hex(body));
        var stack = Assert.Single(rig.Bag.Items);
        Assert.Equal((Stocked, 3), (stack.ItemId, stack.Count));
        Assert.Equal(StartZeny - 3 * 40, rig.Player.Gold);
    }

    [Fact]
    public async Task StockedItem_YouCannotAfford_IsRefused_AndNothingIsCharged()
    {
        await using var rig = await StartAsync();
        var shop = rig.Spawn(ShopNpcAsset, MapId, 1);
        rig.Player.Gold = 100;

        await rig.Handler.HandleAsync(rig.Net.Sessions[0], Buy1334(shop, Stocked, count: 3));   

        var (op, body) = await rig.Net.Readers[0].NextAsync();
        Assert.Equal(ZoneS2COpcodes.OnBuyItem, op);
        Assert.Equal(Hex(ShopItemPackets.BuildBuyRefusal2375(rig.Player.EntityId, 1, 0, Stocked)), Hex(body));
        Assert.Empty(rig.Bag.Items);
        Assert.Equal(100, rig.Player.Gold);
    }

    
    [Fact]
    public void ShopRefusal_WithTheMerchantTableLoaded_NeedsARowOfTheShop()
    {
        var store = SeededStore();
        var npcs = new NpcStateManager(store, NullLogger<NpcStateManager>.Instance);
        npcs.TrackSpawns(MapId, [new SpawnEntry { MapId = MapId, Index = 1, AssetIndex = FieldNpcAsset }]);
        var field = npcs.GetNpc(npcs.AllocateHandle(MapId, 1));

        var own = new BuyItemHandler.MerchantRow(7, ShopNpcAsset, 0, Stocked, 0);
        var foreign = new BuyItemHandler.MerchantRow(8, 99999999, 0, Stocked, 0);

        Assert.Null(BuyItemHandler.ShopRefusal(field, MapId, Stocked, store, own, merchantTableLoaded: true));
        Assert.NotNull(BuyItemHandler.ShopRefusal(field, MapId, Stocked, store, null, merchantTableLoaded: true));   
        Assert.NotNull(BuyItemHandler.ShopRefusal(field, MapId, Stocked, store, foreign, merchantTableLoaded: true));
        
        Assert.Null(BuyItemHandler.ShopRefusal(field, MapId, Stocked, store, null, merchantTableLoaded: false));
        Assert.NotNull(BuyItemHandler.ShopRefusal(field, MapId, FreeItem, store, null, merchantTableLoaded: false));
        Assert.NotNull(BuyItemHandler.ShopRefusal(null, MapId, Stocked, store, own, merchantTableLoaded: false));
    }
}
