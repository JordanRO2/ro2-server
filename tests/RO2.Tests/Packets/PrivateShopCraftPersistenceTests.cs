using RO2.Economy.Infrastructure.Mongo;
using RO2.Economy.Domain;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Persistence.Mongo.Repositories;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;
using RO2.Inventory.Domain;

namespace RO2.Tests.Packets;

/// <summary>
/// Persistence of private-shop craft offers per character (PrivateShopCraftStore, PrivateShopRegistry.LoadCrafts), and
/// the 2646 AnsOpenPrivateShopSellerUI response that lists them.
/// </summary>
public class PrivateShopCraftPersistenceTests
{
    private static List<PrivateShopCraftOffer> OffersOf(byte[] body)
    {
        var blob = body[0] == 1 ? body[2..] : body[3..];
        int count = BitConverter.ToUInt16(blob, 44);
        return Enumerable.Range(0, count)
            .Select(i => new PrivateShopCraftOffer(BitConverter.ToUInt16(blob, 46 + 10 * i), BitConverter.ToUInt64(blob, 48 + 10 * i)))
            .ToList();
    }

    [Theory]
    
    [InlineData("0160000000000000000000000000000000000000000000000000000000000000000000000000000000000000000005002904010000000000000025040100000000000000260401000000000000002704010000000000000028040100000000000000")]
    
    [InlineData("02f60000000000000000000000000000000000000000000000000000000000000000000000000000000000000000001400870501000000000000004806e8030000000000004206e8030000000000004a06e9030000000000004406e8030000000000004c06e8030000000000004106e8030000000000004906e8030000000000004506e8030000000000004b06e8030000000000005506e8030000000000005106e8030000000000005206e8030000000000005406e8030000000000004306e8030000000000004606e8030000000000004e06e8030000000000005006e8030000000000004d06e8030000000000004f06e803000000000000")]
    
    [InlineData("012e00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000")]
    public void SellerCraftList2646_ReproducesRetailFrames(string retailHex)
    {
        var retail = Convert.FromHexString(retailHex);
        Assert.Equal(retail, PrivateShopSellerOpenHandler.BuildAnswer2646(OffersOf(retail)));
    }

    [Fact]
    public void StoredEntries_KeepOrderAndTheWholeU64Price()
    {
        var offers = new List<PrivateShopCraftOffer>
        {
            new(1356, 30000), new(123, 1097), new(1106, 99_999_999), new(1594, ulong.MaxValue),
        };
        var entries = PrivateShopCraftRepository.ToEntries(offers);
        Assert.Equal(-1L, entries[3].Price);    
        Assert.Equal(offers, PrivateShopCraftRepository.FromEntries(entries));
        Assert.Empty(PrivateShopCraftRepository.FromEntries(null));
    }

    [Fact]
    public void LoadCrafts_PutsTheStoredListFirst_AndSetCraftKeepsTheRules()
    {
        var seller = new PlayerState { SessionId = 11, CharacterId = 110, EntityId = 0x0A000011u };
        Assert.False(PrivateShopRegistry.AreCraftsLoaded(seller));
        PrivateShopRegistry.LoadCrafts(seller, new PrivateShopCraftOffer[] { new(1065, 1), new(1061, 1), new(1065, 7) });
        Assert.True(PrivateShopRegistry.AreCraftsLoaded(seller));
        Assert.Equal(new PrivateShopCraftOffer[] { new(1065, 1), new(1061, 1) }, PrivateShopRegistry.GetCrafts(seller));

        PrivateShopRegistry.SetCraft(seller, 1061, 5);   
        PrivateShopRegistry.SetCraft(seller, 1062, 1);   
        Assert.True(PrivateShopRegistry.RemoveCraft(seller, 1065));
        Assert.False(PrivateShopRegistry.RemoveCraft(seller, 1065));
        Assert.Equal(new PrivateShopCraftOffer[] { new(1061, 5), new(1062, 1) }, PrivateShopRegistry.GetCrafts(seller));

        PrivateShopRegistry.BeginSetup(seller);          
        Assert.Equal(2, PrivateShopRegistry.GetCrafts(seller).Count);
    }

    [Fact]
    public async Task EnsureLoaded_ReadsTheStoreOncePerZoneSession()
    {
        var store = new FakeStore();
        store.Saved[120] = new List<PrivateShopCraftOffer> { new(1065, 1), new(1061, 1) };
        var session1 = new PlayerState { SessionId = 12, CharacterId = 120 };
        await PrivateShopCraftStore.EnsureLoadedAsync(session1, store, CancellationToken.None);
        await PrivateShopCraftStore.EnsureLoadedAsync(session1, store, CancellationToken.None);
        Assert.Equal(1, store.Loads);
        Assert.Equal(store.Saved[120], PrivateShopRegistry.GetCrafts(session1));

        
        var session2 = new PlayerState { SessionId = 13, CharacterId = 120 };
        await PrivateShopCraftStore.EnsureLoadedAsync(session2, store, CancellationToken.None);
        Assert.Equal(2, store.Loads);
        Assert.Equal(store.Saved[120], PrivateShopRegistry.GetCrafts(session2));
    }

    [Fact]
    public async Task Changes_LoadFirst_AndSaveTheWholeOrderedList()
    {
        
        
        var store = new FakeStore();
        store.Saved[130] = new List<PrivateShopCraftOffer> { new(64, 1), new(65, 1), new(66, 1) };
        var seller = new PlayerState { SessionId = 14, CharacterId = 130 };

        Assert.True(await PrivateShopCraftStore.RemoveAsync(seller, 65, store, CancellationToken.None));
        Assert.Equal(new PrivateShopCraftOffer[] { new(64, 1), new(66, 1) }, store.Saved[130]);
        Assert.False(await PrivateShopCraftStore.RemoveAsync(seller, 65, store, CancellationToken.None));
        Assert.Equal(1, store.Saves);                   

        await PrivateShopCraftStore.SetAsync(seller, 1612, 1, store, CancellationToken.None);
        await PrivateShopCraftStore.SetAsync(seller, 64, 9, store, CancellationToken.None);
        Assert.Equal(new PrivateShopCraftOffer[] { new(64, 9), new(66, 1), new(1612, 1) }, store.Saved[130]);
        Assert.Equal(1, store.Loads);
    }

    [Fact]
    public async Task ConcurrentAdds_SaveTheirSnapshotsInOrder()
    {
        var store = new FakeStore { SaveDelay = TimeSpan.FromMilliseconds(20) };
        var seller = new PlayerState { SessionId = 15, CharacterId = 140 };
        await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            PrivateShopCraftStore.SetAsync(seller, (ushort)(1600 + i), 1, store, CancellationToken.None)));
        Assert.Equal(8, store.Saved[140].Count);
        Assert.Equal(Enumerable.Range(1, 8), store.History.Select(h => h.Count));   
        Assert.Equal(1, store.Loads);
    }

    private sealed class FakeStore : IPrivateShopCraftRepository
    {
        public readonly Dictionary<uint, List<PrivateShopCraftOffer>> Saved = new();
        public readonly List<List<PrivateShopCraftOffer>> History = new();
        public int Loads, Saves;
        public TimeSpan SaveDelay = TimeSpan.Zero;

        public Task<IReadOnlyList<PrivateShopCraftOffer>> GetOffersAsync(uint characterId, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Loads);
            IReadOnlyList<PrivateShopCraftOffer> list = Saved.TryGetValue(characterId, out var l)
                ? l.ToList() : new List<PrivateShopCraftOffer>();
            return Task.FromResult(list);
        }

        public async Task ReplaceOffersAsync(uint characterId, IReadOnlyList<PrivateShopCraftOffer> offers, CancellationToken ct = default)
        {
            if (SaveDelay > TimeSpan.Zero) await Task.Delay(SaveDelay, ct);
            Saves++;
            Saved[characterId] = offers.ToList();
            History.Add(offers.ToList());
        }
    }
}

/// <summary>
/// Layout of notification 3000 NfyRefineCommissionToBuyer (RefineCommissionPackets): the buyer's handle, the owner's
/// refine price, and the owner's worn refine traits 100, 101 and 102.
/// </summary>
public class RefineCommissionToBuyer3000Tests
{
    [Theory]
    
    [InlineData(0x07788E11u, 1UL, (ushort)2000, (ushort)500, (ushort)3000, "011616008fe4118e78070100000000000000d007f401b80b")]
    
    [InlineData(0x036DD111u, 10UL, (ushort)2000, (ushort)500, (ushort)3000, "011616008fe411d16d030a00000000000000d007f401b80b")]
    
    [InlineData(0x00439A11u, 0UL, (ushort)2000, (ushort)500, (ushort)3000, "011616008fe4119a43000000000000000000d007f401b80b")]
    
    [InlineData(0x0961A611u, 1UL, (ushort)0, (ushort)0, (ushort)0, "011616008fe411a661090100000000000000000000000000")]
    
    [InlineData(0x074A9011u, 0UL, (ushort)0, (ushort)0, (ushort)0, "011616008fe411904a070000000000000000000000000000")]
    public void RefineCommissionToBuyer3000_ReproducesRetailFrames(uint buyer, ulong price, ushort r100, ushort r101, ushort r102, string retailHex) =>
        Assert.Equal(Convert.FromHexString(retailHex),
            RefineCommissionPackets.BuildRefineCommissionToBuyer3000(buyer, price, new RefineCommissionPackets.RefineRates(r100, r101, r102)));

    [Fact]
    public void RatesOf_TakesTheFlatTraits100To102_ClampedToU16()
    {
        var worn = new StatService.EquipStats(
            new Dictionary<byte, int> { [100] = 2000, [101] = 500, [102] = 3000, [0] = 63 },
            new Dictionary<byte, float>());
        Assert.Equal(new RefineCommissionPackets.RefineRates(2000, 500, 3000), RefineCommissionPackets.RatesOf(worn));

        var odd = new StatService.EquipStats(
            new Dictionary<byte, int> { [100] = 70000, [101] = -5 }, new Dictionary<byte, float>());
        Assert.Equal(new RefineCommissionPackets.RefineRates(ushort.MaxValue, 0, 0), RefineCommissionPackets.RatesOf(odd));
        Assert.Equal(default, RefineCommissionPackets.RatesOf(StatService.EquipStats.Empty));
    }

    [Fact]
    public void OnlyHolgrensHammersCarryTheRefineTraits_AtRetailsValues()
    {
        
        using var doc = JsonDocument.Parse(File.ReadAllText(DataFile("TraitInfo.json")));
        var rows = doc.RootElement.EnumerateArray()
            .Where(r => r.GetProperty("Trait_Type").GetInt32() is >= 100 and <= 102)
            .Select(r => (Item: r.GetProperty("ItemID").GetUInt32(), Kind: r.GetProperty("Trait_Kind").GetInt32(),
                Type: r.GetProperty("Trait_Type").GetInt32(), Value: r.GetProperty("Value").GetDouble()))
            .OrderBy(r => r.Item).ThenBy(r => r.Type)
            .ToList();
        Assert.Equal(new[]
        {
            (60000747u, 1, 100, 2000.0), (60000747u, 1, 101, 500.0), (60000747u, 1, 102, 3000.0),
            (60000748u, 1, 100, 2000.0), (60000748u, 1, 101, 500.0), (60000748u, 1, 102, 3000.0),
        }, rows);
    }

    internal static string DataFile(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "data", name);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("no ancestor of " + AppContext.BaseDirectory + " holds data/" + name);
    }
}

/// <summary>
/// Refusal codes of request 1608 ReqThemeCostumeCompose (ThemeCostumeComposeHandler.PlanAsync, BuildRefusal2896).
/// Each case uses request bytes that produce the refusal. Templates absent from the data are stand-ins that keep the
/// real Item_Type.
/// </summary>
public class ThemeCostumeComposeRefusalTests
{
    private const uint Owner = 9;

    [Theory]
    [InlineData((ushort)23052, "0c5a")]   
    [InlineData((ushort)1, "0100")]       
    [InlineData((ushort)23053, "0d5a")]   
    public void Refusal2896_IsTheCodeAnd126ZeroBytes(ushort code, string retailHead) =>
        Assert.Equal(Convert.FromHexString(retailHead + new string('0', 252)), ThemeCostumeComposeHandler.BuildRefusal2896(code));

    [Theory]
    
    
    [InlineData("40000000320101e154df0ea83d040e00000000030301e754df0ea83d040e00000000120201e654df0ea83d040e000000000000000000000000000001000000002a2d2c04")]
    [InlineData("5e000000320101e154df0ea83d040e00000000030301e754df0ea83d040e00000000120201e654df0ea83d040e000000000200330101e254df0ea83d040e00010000340101e354df0ea83d040e0044e919e654df0ea83d040e01000000002a2d2c04")]
    public async Task DeckRecordOfAnotherItem_IsRefusedWith23052(string requestHex)
    {
        var inventory = new FakeInventory(
            Bag(0x0E043DA80EDF54E1, 12711612),   
            Bag(0x0E043DA80EDF54E7, 12711611),   
            Bag(0x0E043DA80EDF54E6, 12711612),
            Bag(0x0E043DA80EDF54E2, 70004012),   
            Bag(0x0E043DA80EDF54E3, 70004013));
        Assert.Equal(ThemeCostumeComposeHandler.ResultDeckMismatch, await RefusalOf(requestHex, inventory));
    }

    [Fact]
    public async Task KeptCostumeWithoutComposeRow_IsRefusedWith1()
    {
        
        
        var inventory = new FakeInventory(
            Bag(0x0E043DA809198B09, 60000478), Bag(0x0E043DA7F529AD95, 70007661),
            Bag(0x0E043DA80E83E927, 12711611), Bag(0x0E043DA80DB6BBF1, 70004013));
        Assert.Equal(ThemeCostumeComposeHandler.ResultKeptCostumeNoRow, await RefusalOf(
            "4f000000020201098b1909a83d040e0000000000020195ad29f5a73d040e0000000040010127e9830ea83d040e000000000100320101f1bbb60da83d040e0001000095ad29f5a73d040e0100000000de889303",
            inventory));
    }

    [Fact]
    public async Task TheSameCostumesWithoutACharm_Compose()
    {
        
        
        var inventory = new FakeInventory(
            Bag(0x0E043DA80F15A025, 70004010), Bag(0x0E043DA7E71CA14A, 70007661),   
            Bag(0x0E043DA664AFD442, 12711611), Bag(0x0E043DA80F15A024, 70004013));
        Assert.Equal(ThemeCostumeComposeHandler.ResultKeptCostumeNoRow, await RefusalOf(
            "4f00000041010125a0150fa83d040e000000001d040142d4af64a63d040e000000000802014aa11ce7a73d040e0000000001003d010124a0150fa83d040e000100004aa11ce7a73d040e01000000002a2d2c04",
            inventory));
        Assert.Equal(0, await RefusalOf(
            "4000000041010125a0150fa83d040e000000000802014aa11ce7a73d040e000000001d040142d4af64a63d040e000000000000000000000000000001000000002a2d2c04",
            inventory));
    }

    [Fact]
    public async Task ItemsNotInTheBag_AreRefusedWith23053()
    {
        
        var inventory = new FakeInventory();
        Assert.Equal(ThemeCostumeComposeHandler.ResultNotComposable, await RefusalOf(
            "40000000010201dda5250ea83d040e000000003401012f93250ea83d040e00000000420101d0a5250ea83d040e00000000000000000000000000000100000000de889303",
            inventory));
    }

    private static InventoryItem Bag(ulong uid, uint itemId) =>
        new() { Id = uid, ItemId = itemId, CharacterId = Owner, StorageType = 0, Count = 1 };

    private static async Task<ushort> RefusalOf(string requestHex, IInventoryRepository inventory)
    {
        var request = ThemeCostumeComposeHandler.ParseRequest(Convert.FromHexString(requestHex));
        Assert.NotNull(request);
        var (plan, refusal) = await ThemeCostumeComposeHandler.PlanAsync(request, Owner, await Store.Value, inventory);
        Assert.Equal(refusal == 0, plan is not null);
        return refusal;
    }

    private static readonly Lazy<Task<GameDataStore>> Store = new(LoadAsync);

 /// <summary>The shipped rows these requests touch: the two decks' ComposeRandomInfo, the items' Item_Type, and
 /// the first ComposeInfo row (theme 100011, sockets 12711611..14).</summary>
    private static async Task<GameDataStore> LoadAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ro2-costumecompose-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "ItemInfo.json"), """
                [{"ID":60000478,"Item_Type":45,"Stack_Max":99},{"ID":70004010,"Item_Type":46,"Stack_Max":99},
                 {"ID":70004012,"Item_Type":47,"Stack_Max":99},{"ID":70004013,"Item_Type":48,"Stack_Max":99},
                 {"ID":70007661,"Item_Type":3,"Stack_Max":1},{"ID":12711611,"Item_Type":3,"Stack_Max":1},
                 {"ID":12711612,"Item_Type":3,"Stack_Max":1}]
                """);
            await File.WriteAllTextAsync(Path.Combine(dir, "ComposeRandomInfo.json"), """
                [{"ItemID":60000478,"Ratio_Socket_0":5000,"Ratio_Socket_1":3000,"Ratio_Socket_2":1500,"Ratio_Socket_3":500},
                 {"ItemID":70004010,"Ratio_Socket_0":5000,"Ratio_Socket_1":3000,"Ratio_Socket_2":1500,"Ratio_Socket_3":500}]
                """);
            await File.WriteAllTextAsync(Path.Combine(dir, "ComposeInfo.json"), """
                [{"Theme_Type":100011,"Type":16,"Item_Socket_0":12711611,"Item_Socket_1":12711612,"Item_Socket_2":12711613,"Item_Socket_3":12711614,"Ratio":100}]
                """);
            var store = new GameDataStore();
            await store.LoadAsync(dir);
            return store;
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private sealed class FakeInventory : IInventoryRepository
    {
        private readonly List<InventoryItem> _items;
        public FakeInventory(params InventoryItem[] items) => _items = items.ToList();
        public Task<IReadOnlyList<InventoryItem>> GetByCharacterAsync(uint characterId) =>
            Task.FromResult<IReadOnlyList<InventoryItem>>(_items.Where(i => i.CharacterId == characterId).ToList());
        public Task<IReadOnlyList<InventoryItem>> GetEquipmentAsync(uint characterId) =>
            Task.FromResult<IReadOnlyList<InventoryItem>>(Array.Empty<InventoryItem>());
        public Task<InventoryItem?> GetByIdAsync(ulong id) => Task.FromResult(_items.FirstOrDefault(i => i.Id == id));
        public Task AddItemAsync(InventoryItem item) { _items.Add(item); return Task.CompletedTask; }
        public Task RemoveItemAsync(ulong itemId) { _items.RemoveAll(i => i.Id == itemId); return Task.CompletedTask; }
        public Task UpdateItemAsync(InventoryItem item) => Task.CompletedTask;
        public Task<ulong> GetNextIdAsync() => Task.FromResult(_items.Count == 0 ? 1UL : _items.Max(i => i.Id) + 1);
        public Task<int> RepairSlotsAsync(uint characterId) => Task.FromResult(0);
    }
}
