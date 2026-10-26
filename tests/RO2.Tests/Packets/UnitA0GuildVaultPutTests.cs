using System.Text.Json;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.GameData.Tables;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Services;
using Xunit;
using RO2.Inventory.Domain;

namespace RO2.Tests.Packets;

/// <summary>
/// Guild vault put: 1485 ReqGuildStorageItemPut, 2379 OnDestroyItem, 2670 AnsGuildStorageItemPut and
/// 2675 NfyAddGuildStorage_Item, the vault capacity (<see cref="GuildVaultCapacity"/>) and the full-vault
/// refusal (2379, then 2333, then 2670 with result code 29).
/// </summary>
/// <remarks>
/// The guild tests run on GuildService.Instance without a database: InitAsync is never called, so every
/// write-through is skipped. Each test disbands the guild it creates.
/// </remarks>
public class UnitA0GuildVaultPutTests
{
    private static int _nextChar = 1_981_000_000;
    private static uint NewCharId() => (uint)Interlocked.Increment(ref _nextChar);

    private static string Hex(ReadOnlySpan<byte> b) => Convert.ToHexString(b).ToLowerInvariant();

    
    [Fact]
    public void Put2379_MatchesRetailByteForByte()
    {
        
        
        const string retail =
            "028a008a00c7e2112e4507000001003a05ff0ca83d040e65dbfe0000000003040000000000000000000000000000000000000000" +
            "00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
            "00000000000000000000000000000000000000000000000000000000000000000000000000";
        var ours = GuildStorageItemPutHandler.BuildDestroyItem2379(0x07452E11u, 0x0E043DA80CFF053AUL, 16702309u, 3, 4);
        Assert.Equal(141, ours.Length);
        Assert.Equal(retail, Hex(ours));
    }

    [Fact]
    public void Put2379_LeavesOutTheDurabilityTheItemCarries()
    {
        
        
        
        var vault = Convert.FromHexString(
            "3a05ff0ca83d040e65dbfe00102701656300000000000000000000000000000000000000000000000000000000000000000000000000" +
            "000000000000000000000000000000986c00001800d807d505a80fbd00b9369efea90f9dfda90f58fd00000000000000000000000000" +
            "000000000000000000000000000000000000");
        Assert.Equal(126, vault.Length);
        var rec = GuildStorageItemPutHandler.BuildDestroyItem2379(0x07452E11u, 0x0E043DA80CFF053AUL, 16702309u, 3, 4)
            .AsSpan(15).ToArray();
        Assert.Equal(vault.AsSpan(0, 12).ToArray(), rec.AsSpan(0, 12).ToArray());   
        Assert.Equal((ushort)10000, BitConverter.ToUInt16(vault, 12));
        Assert.Equal((ushort)0, BitConverter.ToUInt16(rec, 12));
        Assert.Equal(0, rec[14]);                                                    
        Assert.All(rec.AsSpan(17).ToArray(), b => Assert.Equal(0, b));
    }

    
    public static TheoryData<string, string, uint, ulong, uint, int, short, short> FullVaultFrames => new()
    {
        
        
        {
            "028a008a00c7e211c0f303000001005431f30ea83d040e98dbfe0000000002120000000000000000000000000000000000000000000000000000000000000000",
            "000001005431f30ea83d040e98dbfe00102701021200000000000000000000000000000000000000000000000000000000000000000000000000000000000000",
            0x03F3C011u, 0x0E043DA80EF33154UL, 16702360u, 1, 10000, 20 + 0x12
        },
        
        
        {
            "028a008a00c7e211196f01000001000a7c87e6a73d040e4f384201000000013c0000000000000000000000000000000000000000000000000000000000000000",
            "000001000a7c87e6a73d040e4f3842010000fc013c00000000000000000000000000000000000000000000000000000000000000000000000000000000000000",
            0x016F1911u, 0x0E043DA7E6877C0AUL, 21117007u, 252, 0, 0x3c - 50
        },
    };

    [Theory]
    [MemberData(nameof(FullVaultFrames))]
    public void VaultFull_2379And2333_MatchRetailsFirst64Bytes(string retail2379, string retail2333, uint handle, ulong uid, uint template, int count, short durability, short flatSlot)
    {
        var item = new InventoryItem
        {
            Id = uid, ItemId = template, Count = count, Durability = durability, StorageType = 0, Slot = flatSlot,
        };
        var bag = Convert.FromHexString(retail2379)[15 + 15];
        var slot = Convert.FromHexString(retail2379)[15 + 16];

        var ours2379 = GuildStorageItemPutHandler.BuildDestroyItem2379(handle, item.Id, item.ItemId, bag, slot);
        Assert.Equal(retail2379, Hex(ours2379.AsSpan(0, 64)));
        Assert.All(ours2379.AsSpan(64).ToArray(), b => Assert.Equal(0, b));   

        
        var ours2333 = LootNotifyPackets.BuildPickupItem2333(ItemPackets.BuildItemRecord126(item),
            resultCode: 0, pickupKind: GuildStorageItemPutHandler.PickupKindGuildReturn);
        Assert.Equal(130, ours2333.Length);
        Assert.Equal(retail2333, Hex(ours2333.AsSpan(0, 64)));
    }

    [Fact]
    public void ResultFor_MapsTheRefusals()
    {
        Assert.Equal((ushort)0, GuildStorageItemPutHandler.ResultFor(GuildService.DepositRefusal.None));
        Assert.Equal((ushort)29, GuildStorageItemPutHandler.ResultFor(GuildService.DepositRefusal.VaultFull));
        Assert.Equal((ushort)28, GuildStorageItemPutHandler.ResultFor(GuildService.DepositRefusal.NotMoved));
    }

    
    [Fact]
    public async Task SlotCapacity_IsTheGuildsProsperityLevel()
    {
        var table = GuildSkillLearnPackets.Index(await LoadAsync<GuildSkillLevel>("Guild_Skill_Level.json"));
        var config = await LoadAsync<ConfigDef>("Config_Def.json");

        Assert.Equal(0, GuildVaultCapacity.SlotCapacity(Array.Empty<uint>(), table, config));
        Assert.Equal(20, GuildVaultCapacity.SlotCapacity(new uint[] { 120401 }, table, config));
        Assert.Equal(40, GuildVaultCapacity.SlotCapacity(new uint[] { 120402 }, table, config));
        Assert.Equal(60, GuildVaultCapacity.SlotCapacity(new uint[] { 120401, 120403 }, table, config));
        Assert.Equal(80, GuildVaultCapacity.SlotCapacity(new uint[] { 120404 }, table, config));
        Assert.Equal(100, GuildVaultCapacity.SlotCapacity(new uint[] { 120405 }, table, config));
        
        Assert.Equal(0, GuildVaultCapacity.SlotCapacity(new uint[] { 120505, 999999 }, table, config));

        
        Assert.Equal(100, GuildVaultCapacity.MaxCells(config));
        Assert.Equal("20", config.Single(c => c.type == 11).Value);
        var lowCeiling = new[] { new ConfigDef { type = GuildVaultCapacity.ConfigMaxVaultCells, Value = "60" } };
        Assert.Equal(60, GuildVaultCapacity.SlotCapacity(new uint[] { 120405 }, table, lowCeiling));
        Assert.Equal(GuildVaultCapacity.AddressableCells, GuildVaultCapacity.MaxCells(Array.Empty<ConfigDef>()));
    }

    [Fact]
    public void LowestFreeCell_FollowsRetailsCells()
    {
        
        
        var storage = Cells(Enumerable.Range(0, 42).Concat(Enumerable.Range(50, 50)));
        Assert.Equal(42, GuildVaultCapacity.LowestFreeCell(storage, 100));
        storage.AddRange(Cells(Enumerable.Range(42, 8)));
        Assert.Equal(-1, GuildVaultCapacity.LowestFreeCell(storage, 100));
        
        Assert.Equal(-1, GuildVaultCapacity.LowestFreeCell(Cells(Enumerable.Range(0, 60)), 60));
        Assert.Equal(-1, GuildVaultCapacity.LowestFreeCell(new List<GuildStorageItem>(), 0));
    }

    
    [Fact]
    public async Task Deposit_TakesTheLowestFreeCell()
    {
        var charId = NewCharId();
        var g = await NewGuildAsync(charId);
        try
        {
            lock (g.Gate) g.Storage.AddRange(Cells(Enumerable.Range(0, 42).Concat(Enumerable.Range(50, 50))));
            var inv = new FakeInventory();
            var item = inv.Put(charId, 21117007, 252, 10);

            var (stored, refusal) = await GuildService.Instance.DepositAsync(g, item, inv, 100);

            Assert.Equal(GuildService.DepositRefusal.None, refusal);
            Assert.Equal((byte)42, Assert.IsType<GuildStorageItem>(stored).StorageSlot);
            Assert.Empty(inv.Items);
            lock (g.Gate) Assert.Equal(93, g.Storage.Count);
        }
        finally { await GuildService.Instance.DisbandAsync(g); }
    }

    [Fact]
    public async Task Deposit_IntoAFullVault_IsRefusedBeforeTheItemLeaves()
    {
        var charId = NewCharId();
        var g = await NewGuildAsync(charId);
        try
        {
            lock (g.Gate) g.Storage.AddRange(Cells(Enumerable.Range(0, 100)));
            var inv = new FakeInventory();
            var item = inv.Put(charId, 16702360, 1, 38);

            var (stored, refusal) = await GuildService.Instance.DepositAsync(g, item, inv, 100);

            Assert.Equal(GuildService.DepositRefusal.VaultFull, refusal);
            Assert.Null(stored);
            Assert.Equal(0, inv.Removes);
            Assert.Same(item, Assert.Single(inv.Items));
            lock (g.Gate) Assert.Equal(100, g.Storage.Count);
        }
        finally { await GuildService.Instance.DisbandAsync(g); }
    }

    [Fact]
    public async Task Deposit_LosingTheLastCellToAnotherPut_PutsTheItemBack()
    {
        var charId = NewCharId();
        var g = await NewGuildAsync(charId);
        try
        {
            lock (g.Gate) g.Storage.AddRange(Cells(Enumerable.Range(0, 99)));
            var inv = new FakeInventory();
            var item = inv.Put(charId, 16702360, 1, 38);
            
            inv.OnRemove = () => { lock (g.Gate) g.Storage.AddRange(Cells(new[] { 99 })); };

            var (stored, refusal) = await GuildService.Instance.DepositAsync(g, item, inv, 100);

            Assert.Equal(GuildService.DepositRefusal.VaultFull, refusal);
            Assert.Null(stored);
            var back = Assert.Single(inv.Items);
            Assert.Equal(item.Id, back.Id);
            Assert.Equal((short)38, back.Slot);
            lock (g.Gate)
            {
                Assert.Equal(100, g.Storage.Count);
                Assert.DoesNotContain(g.Storage, s => s.Uid == item.Id);
            }
        }
        finally { await GuildService.Instance.DisbandAsync(g); }
    }

    
    private static async Task<Guild> NewGuildAsync(uint charId)
    {
        var (guild, refusal) = await GuildService.Instance.CreateAsync(charId, "Depositor", 60, 6, $"Vault {Guid.NewGuid():N}");
        Assert.Equal(GuildService.CreateRefusal.None, refusal);
        return Assert.IsType<Guild>(guild);
    }

    private static List<GuildStorageItem> Cells(IEnumerable<int> cells)
        => cells.Select(c => new GuildStorageItem { Uid = 9_000_000UL + (ulong)c, ItemId = 21117007, StorageSlot = (byte)c }).ToList();

    private static async Task<List<T>> LoadAsync<T>(string file)
    {
        await using var fs = File.OpenRead(DataFile(file));
        return (await JsonSerializer.DeserializeAsync<List<T>>(fs))!;
    }

    private static string DataFile(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "data", name);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("no ancestor of " + AppContext.BaseDirectory + " holds data/" + name);
    }

    private sealed class FakeInventory : IInventoryRepository
    {
        private ulong _nextId = 0x0E043DA800000001UL;
        public readonly List<InventoryItem> Items = new();
        public int Removes;
        public Action? OnRemove;

        public InventoryItem Put(uint charId, uint template, int count, short flatSlot)
        {
            var item = new InventoryItem
            {
                Id = _nextId++, CharacterId = charId, ItemId = template, Count = count, StorageType = 0, Slot = flatSlot,
            };
            Items.Add(item);
            return item;
        }

        public Task<IReadOnlyList<InventoryItem>> GetByCharacterAsync(uint characterId) =>
            Task.FromResult<IReadOnlyList<InventoryItem>>(Items.Where(i => i.CharacterId == characterId).ToList());
        public Task<IReadOnlyList<InventoryItem>> GetEquipmentAsync(uint characterId) =>
            Task.FromResult<IReadOnlyList<InventoryItem>>(Array.Empty<InventoryItem>());
        public Task<InventoryItem?> GetByIdAsync(ulong id) => Task.FromResult(Items.FirstOrDefault(i => i.Id == id));
        public Task AddItemAsync(InventoryItem item)
        {
            if (item.Id == 0) item.Id = _nextId++;
            Items.Add(item);
            return Task.CompletedTask;
        }
        public Task RemoveItemAsync(ulong itemId)
        {
            Removes++;
            Items.RemoveAll(i => i.Id == itemId);
            OnRemove?.Invoke();
            return Task.CompletedTask;
        }
        public Task UpdateItemAsync(InventoryItem item) => Task.CompletedTask;
        public Task<ulong> GetNextIdAsync() => Task.FromResult(_nextId++);
        public Task<int> RepairSlotsAsync(uint characterId) => Task.FromResult(0);
    }
}
