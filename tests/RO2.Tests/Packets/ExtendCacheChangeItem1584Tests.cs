using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.GameData;
using RO2.GameData.Tables;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Services;
using Xunit;
using RO2.Inventory.Domain;

namespace RO2.Tests.Packets;

/// <summary>
/// Tests 1584 ReqExtendCacheChangeItem and its 2851 AnsExtendCacheChangeItem answer
/// (<see cref="ItemChangeExchange"/>). The tests run on a store holding the shipped rows they need (ItemInfo,
/// ItemChangeInfo, RandomSet). The draw is fixed through the injected random source, so each test selects
/// the row its scenario requires.
/// </summary>
/// <remarks>
/// Byte-exact answers use templates with an ItemInfo Abili_val of 0, so they do not cover Item_Type 6
/// material gains, which carry the template's Abili_val at record offset 69. That field belongs to the shared
/// record writer (see the ItemChangeExchange remarks).
/// </remarks>
public class ExtendCacheChangeItem1584Tests
{
    private const uint Owner = 7;

    private const uint Hammer = 30000270;      
    private const uint Key = 60000475;         
    private const uint Box = 30000269;         
    private const uint Elixir = 30000297;      
    private const uint Rune = 22261025;
    private const uint Polinium = 70000657;
    private const uint Staff = 91204331;       
    private const uint Wing = 21330011;        
    private const uint Filler = 30000272;      
    private const uint BigBag = 70000033;      

    private static readonly Lazy<Task<GameDataStore>> Store = new(LoadAsync);

    private static async Task<GameDataStore> LoadAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ro2-itemchange-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            
            await File.WriteAllTextAsync(Path.Combine(dir, "ItemInfo.json"), """
                [{"ID":30000270,"Item_Type":55,"Stack_Max":99,"RandomSet_ID":224},
                 {"ID":60000475,"Item_Type":55,"Stack_Max":99,"RandomSet_ID":223},
                 {"ID":30000269,"Stack_Max":99},
                 {"ID":30000297,"Item_Type":42,"Stack_Max":99,"RandomSet_ID":92},
                 {"ID":22261025,"Item_Type":29,"Stack_Max":1},
                 {"ID":70000657,"Stack_Max":99},
                 {"ID":91204331,"Stack_Max":1,"Durability":10000,"Bind_Type":2},
                 {"ID":21330011,"Stack_Max":99},
                 {"ID":30000272,"Stack_Max":99},
                 {"ID":70000033,"Item_Type":7,"Stack_Max":1,"Bag_Size":35}]
                """);
            
            await File.WriteAllTextAsync(Path.Combine(dir, "ItemChangeInfo.json"), """
                [{"ID":30000297,"Change_Type":2,"Target_ItemID":22261025,"Target_ItemCount":1},
                 {"ID":30000270,"Change_Type":3,"Target_ItemID":30000269,"Target_ItemCount":1},
                 {"ID":60000475,"Change_Type":4,"Target_ItemID":30000269,"Target_ItemCount":1}]
                """);
            
            
            await File.WriteAllTextAsync(Path.Combine(dir, "RandomSet.json"), """
                [{"ID":223,"Item":91204331,"Package":1,"Ratio":450,"NationEnable":2},
                 {"ID":223,"Item":70000657,"Package":20,"Ratio":5000,"NationEnable":229},
                 {"ID":223,"Item":91204331,"Package":1,"Ratio":450,"NationEnable":229},
                 {"ID":223,"Item":91204331,"Package":1,"Ratio":454,"NationEnable":8},
                 {"ID":224,"Item":21330011,"Package":1,"Ratio":10000,"NationEnable":2},
                 {"ID":224,"Item":21330011,"Package":1,"Ratio":8200,"NationEnable":229},
                 {"ID":224,"Item":70000657,"Package":5,"Ratio":5000,"NationEnable":229},
                 {"ID":224,"Item":70000657,"Package":5,"Ratio":5000,"NationEnable":229}]
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

    private static byte[] Hex(string s) => Convert.FromHexString(s);

    private static InventoryItem Row(ulong uid, uint itemId, int count, short flat, byte storage = 0, short durability = 0) =>
        new() { Id = uid, CharacterId = Owner, ItemId = itemId, Count = count, Slot = flat, StorageType = storage, Durability = durability };

    
 /// <summary>Encrypted (AES) 1584 request body of 20 bytes.</summary>
    [Fact]
    public void Request_ParsesTheArchiveFrame()
    {
        Assert.True(ItemChangeExchange.TryParseRequest(Hex("03052f49030ea83d040e03063da60b0ea83d040e"), out var r));
        Assert.Equal((byte)3, r.BagA);
        Assert.Equal((byte)5, r.SlotA);
        Assert.Equal(0x0E043DA80E03492FUL, r.UidA);
        Assert.Equal((byte)3, r.BagB);
        Assert.Equal((byte)6, r.SlotB);
        Assert.Equal(0x0E043DA80E0BA63DUL, r.UidB);
        Assert.False(ItemChangeExchange.TryParseRequest(new byte[19], out _));
    }

    
 /// <summary>
 /// A successful exchange: a Pandora's Key (Change_Type 4) on the last box gains a Fearsome Pandora's Staff
 /// into the cell of the box that was used up. The bag is full up to that cell, with the key and the box in
 /// the cells the request names.
 /// </summary>
    [Fact]
    public async Task Key_OnLastBox_MatchesTheArchiveAnswer()
    {
        var store = await Store.Value;
        var inv = new FakeInventory(nextId: 0x0E043DA80E0C8C90UL);
        inv.Items.Add(Row(900, BigBag, 1, 0, storage: 3));
        inv.Items.Add(Row(901, BigBag, 1, 1, storage: 3));
        for (short flat = 0; flat < 60; flat++) inv.Items.Add(Row(1000UL + (ulong)flat, Filler, 1, flat));
        inv.Items.Add(Row(0x0E043DA80E03492FUL, Key, 2, 60));   
        inv.Items.Add(Row(0x0E043DA80E0BA63DUL, Box, 1, 61));   

        
        var outcome = await ItemChangeExchange.ExchangeAsync(inv, store, Owner, 0x0E043DA80E03492FUL, 0x0E043DA80E0BA63DUL, _ => 5000);

        var retail = Hex("000004012f49030ea83d040e3da60b0ea83d040e00908c0c0ea83d040eebaa6f05102701030600000000000000000000020000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000");
        Assert.Equal(ItemChangeExchange.ResultOk, outcome.Result);
        Assert.Equal(retail.Length, outcome.Body.Length);
        
        
        
        Assert.Equal(retail.AsSpan(0, 48).ToArray(), outcome.Body.AsSpan(0, 48).ToArray());
        Assert.Equal(retail.AsSpan(49).ToArray(), outcome.Body.AsSpan(49).ToArray());
        Assert.Equal(retail[48], ItemPackets.RecordBindFlags(bindType: 2, storageType: 0));

        Assert.Equal(1, inv.Items.Single(i => i.Id == 0x0E043DA80E03492FUL).Count);
        Assert.DoesNotContain(inv.Items, i => i.Id == 0x0E043DA80E0BA63DUL);
        var staff = inv.Items.Single(i => i.ItemId == Staff);
        Assert.Equal((short)61, staff.Slot);
        Assert.Equal((short)10000, staff.Durability);
    }

 /// <summary>
 /// A successful Pandora's Hammer exchange on a box gains 5 Polinium onto the existing stack, which grows
 /// from 91 to 96 under its own uid.
 /// </summary>
    [Fact]
    public async Task Hammer_RestacksPolinium_MatchesTheLiveAnswerByteForByte()
    {
        var store = await Store.Value;
        var inv = new FakeInventory(nextId: 1);
        inv.Items.Add(Row(0x0E043DA812FEE665UL, Hammer, 7, 0));
        inv.Items.Add(Row(0x0E043DA80D7B8D43UL, Box, 7, 1));
        inv.Items.Add(Row(0x0E043DA812FEE581UL, Polinium, 91, 4));   

        
        var outcome = await ItemChangeExchange.ExchangeAsync(inv, store, Owner, 0x0E043DA812FEE665UL, 0x0E043DA80D7B8D43UL, _ => 8200);

        var retail = Hex("0000030665e6fe12a83d040e438d7b0da83d040e0681e5fe12a83d040e11202c040000600136" + new string('0', 2 * 109));
        Assert.Equal(retail, outcome.Body);
        Assert.Equal(Polinium, outcome.GainedItemId);
        Assert.Equal(5, outcome.GainedCount);
        Assert.Equal(96, inv.Items.Single(i => i.Id == 0x0E043DA812FEE581UL).Count);
        Assert.Equal(3, inv.Items.Count);   
    }

 /// <summary>
 /// A refusal with code 8 (no space in the bag), answering a hammer used on a box. Reproduced with a full
 /// main bag where neither stack empties and the drawn item has no stack to join. The u16 after the code is
 /// not read by the client and is 0 here.
 /// </summary>
    [Fact]
    public async Task FullBag_RefusesWith8_AndConsumesNothing()
    {
        var store = await Store.Value;
        var inv = new FakeInventory(nextId: 1);
        for (short flat = 2; flat < BagCapacity.MainBagCells; flat++) inv.Items.Add(Row(2000UL + (ulong)flat, Filler, 1, flat));
        inv.Items.Add(Row(0x0E043DA80EE68119UL, Hammer, 5, 0));
        inv.Items.Add(Row(0x0E043DA80EC5472CUL, Box, 5, 1));

        var outcome = await ItemChangeExchange.ExchangeAsync(inv, store, Owner, 0x0E043DA80EE68119UL, 0x0E043DA80EC5472CUL, _ => 0);

        var retail = Hex("08002a001981e60ea83d040e" + new string('0', 2 * 135));
        Assert.Equal(ItemChangeExchange.ResultBagFull, outcome.Result);
        Assert.Equal(retail.AsSpan(0, 2).ToArray(), outcome.Body.AsSpan(0, 2).ToArray());
        Assert.Equal(new byte[2], outcome.Body.AsSpan(2, 2).ToArray());
        Assert.Equal(retail.AsSpan(4).ToArray(), outcome.Body.AsSpan(4).ToArray());
        Assert.Equal(5, inv.Items.Single(i => i.Id == 0x0E043DA80EE68119UL).Count);
        Assert.Equal(5, inv.Items.Single(i => i.Id == 0x0E043DA80EC5472CUL).Count);
    }

 /// <summary>A key or box that empties frees its cell for the gain, since the client removes both before
 /// it adds the record. A full main bag still takes the gained item when the box is the last one.</summary>
    [Fact]
    public async Task FullBag_TakesTheGain_WhenTheBoxEmpties()
    {
        var store = await Store.Value;
        var inv = new FakeInventory(nextId: 77);
        for (short flat = 2; flat < BagCapacity.MainBagCells; flat++) inv.Items.Add(Row(3000UL + (ulong)flat, Filler, 1, flat));
        inv.Items.Add(Row(11, Hammer, 5, 0));
        inv.Items.Add(Row(12, Box, 1, 1));

        var outcome = await ItemChangeExchange.ExchangeAsync(inv, store, Owner, 11, 12, _ => 0);

        Assert.Equal(ItemChangeExchange.ResultOk, outcome.Result);
        Assert.Equal((byte)4, outcome.Body[3]);    
        Assert.Equal((byte)0, outcome.Body[20]);   
        var wing = inv.Items.Single(i => i.ItemId == Wing);
        Assert.Equal((short)1, wing.Slot);
        Assert.Equal(77UL, BinaryPrimitives.ReadUInt64LittleEndian(outcome.Body.AsSpan(21)));
    }

 /// <summary>Refusals keep the 147-byte shape, echo uidA and change nothing: a Change_Type 2 pair, a
 /// hammer or key used on something that is not its box (client codes 10 and 11), and a uid that is not the
 /// caller's bag row (code 2).</summary>
    [Fact]
    public async Task Refusals_UseTheClientCodes()
    {
        var store = await Store.Value;
        var inv = new FakeInventory(nextId: 1);
        inv.Items.Add(Row(21, Elixir, 3, 0));
        inv.Items.Add(Row(22, Rune, 1, 1));
        inv.Items.Add(Row(23, Hammer, 3, 2));
        inv.Items.Add(Row(24, Key, 3, 3));
        inv.Items.Add(Row(25, Box, 3, 4));
        inv.Items.Add(new InventoryItem { Id = 26, CharacterId = Owner + 1, ItemId = Box, Count = 3, Slot = 5 });

        async Task<ItemChangeExchange.Outcome> Run(ulong a, ulong b) =>
            await ItemChangeExchange.ExchangeAsync(inv, store, Owner, a, b, _ => 0);

        var elixir = await Run(21, 22);
        Assert.Equal(ItemChangeExchange.ResultCannotChange, elixir.Result);
        Assert.Equal(ItemChangeExchange.AnswerLength, elixir.Body.Length);
        Assert.Equal(21UL, BinaryPrimitives.ReadUInt64LittleEndian(elixir.Body.AsSpan(4)));
        Assert.All(elixir.Body.AsSpan(12).ToArray(), x => Assert.Equal((byte)0, x));

        Assert.Equal(ItemChangeExchange.ResultWrongMalletTarget, (await Run(23, 22)).Result);
        Assert.Equal(ItemChangeExchange.ResultWrongKeyTarget, (await Run(24, 22)).Result);
        Assert.Equal(ItemChangeExchange.ResultCannotChange, (await Run(25, 23)).Result);   
        Assert.Equal(ItemChangeExchange.ResultNoItem, (await Run(23, 26)).Result);         
        Assert.Equal(ItemChangeExchange.ResultNoItem, (await Run(23, 99)).Result);         
        Assert.Equal(ItemChangeExchange.ResultNoItem, (await Run(25, 25)).Result);         

        Assert.Equal(new[] { 3, 1, 3, 3, 3, 3 }, inv.Items.Select(i => i.Count).ToArray());
    }

    
 /// <summary>The distinct gained templates of every successful answer, read from the gained record at
 /// body+21+8. Change_Type 3 and Change_Type 4 answers are both covered.</summary>
    private static readonly uint[] HammerGains =
    {
        13003161, 13003162, 13003163, 13003164, 13003165, 13003166, 13003167, 13003168, 21000134, 21330011,
        21330019, 30000272, 30000276, 30000277, 30000293, 30000297, 31000001, 31000002, 31000003, 31000004,
        31000005, 31100001, 31100002, 31100003, 31100004, 31100005, 31200001, 31200002, 31200003, 31200004,
        31200005, 31300001, 31300002, 31300003, 31300004, 31300005, 31400001, 31400002, 31400003, 31400004,
        31400005, 31400006, 31400007, 31400008, 31400009, 31400010, 60000227, 60000377, 70000657, 91104311,
        91104322, 91104331, 91154311, 91154322, 91154331, 91204311, 91204322, 91204331, 91254311, 91254322,
        91304311, 91304322, 91304331, 91354311, 91354322, 91404311, 91404322, 91404331, 91454311, 91454322,
        91454331, 91504311, 91504322, 91554311, 91554322, 91554331, 91604322, 91604331, 91654322, 91654331,
        97104311, 97104322, 97104331, 97654322, 97654331,
    };

    private static readonly uint[] KeyGains =
    {
        13003161, 13003163, 13003167, 13003168, 21000136, 21000137, 21000142, 21000143, 22300201, 30000271,
        30000272, 30000274, 30000275, 30000293, 60000227, 60000377, 70000657, 91104322, 91114341, 91204322,
        91204331, 91254322, 91254341, 91304331, 91304341, 91354322, 91354341, 91404322, 91404341, 91454322,
        91454331, 91504322, 91504341, 91554322, 91604322, 91654322, 97114341, 97654322,
    };

    private static string DataDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "data");
            if (File.Exists(Path.Combine(candidate, "RandomSet.json")))
                return candidate;
        }
        throw new DirectoryNotFoundException("no ancestor of " + AppContext.BaseDirectory + " holds data/RandomSet.json");
    }

    private static readonly JsonSerializerOptions ShippedJson = new() { PropertyNameCaseInsensitive = true };

    private static async Task<List<T>> ShippedAsync<T>(string file)
    {
        await using var fs = File.OpenRead(Path.Combine(DataDir(), file));
        return (await JsonSerializer.DeserializeAsync<List<T>>(fs, ShippedJson))!;
    }

    [Fact]
    public async Task Candidates_HoldEveryCapturedGain_AndOnlyNationBit0Rows()
    {
        var table = await ShippedAsync<RandomSet>("RandomSet.json");
        var hammer = ItemChangeExchange.Candidates(table, 224);
        var key = ItemChangeExchange.Candidates(table, 223);

        Assert.Equal(92, hammer.Count);
        Assert.Equal(75, key.Count);
        Assert.All(hammer.Concat(key), r => Assert.True((r.NationEnable & 1) != 0 && r.Ratio > 0));
        Assert.All(HammerGains, id => Assert.Contains(hammer, r => r.Item == id));
        Assert.All(KeyGains, id => Assert.Contains(key, r => r.Item == id));
        
        Assert.All(hammer.Where(r => r.Item == Polinium), r => Assert.Equal((byte)5, r.Package));
        Assert.All(key.Where(r => r.Item == Polinium), r => Assert.Equal((byte)20, r.Package));
        Assert.All(hammer.Concat(key), r => Assert.Equal((byte)0, r.Broadcast));

        
        var bit1 = table.Where(r => r.ID == 224 && (r.NationEnable & 2) != 0 && r.Ratio > 0).Select(r => r.Item).ToHashSet();
        Assert.Contains(HammerGains, id => !bit1.Contains(id));
    }

    [Fact]
    public async Task Candidates_OfEveryShippedKey_FitOneStack()
    {
        var table = await ShippedAsync<RandomSet>("RandomSet.json");
        var changes = await ShippedAsync<ItemChangeInfo>("ItemChangeInfo.json");
        var items = (await ShippedAsync<ItemData>("ItemInfo.json")).GroupBy(i => i.ID).ToDictionary(g => g.Key, g => g.First());
        foreach (var row in changes.Where(c => c.Change_Type is ItemChangeExchange.ChangeTypeHammer or ItemChangeExchange.ChangeTypeKey))
        {
            var set = items[row.ID].RandomSet_ID;
            Assert.NotEmpty(ItemChangeExchange.Candidates(table, set));
            foreach (var r in ItemChangeExchange.Candidates(table, set))
                Assert.True(r.Package <= Math.Max((byte)1, items[r.Item].Stack_Max), $"set {set} item {r.Item} package {r.Package}");
        }
    }

    [Fact]
    public void Pick_WalksRatioBands()
    {
        var rows = new List<RandomSet>
        {
            new() { ID = 1, Item = 10, Ratio = 3 },
            new() { ID = 1, Item = 20, Ratio = 2 },
        };
        Assert.Equal(10u, ItemChangeExchange.Pick(rows, 0)!.Item);
        Assert.Equal(10u, ItemChangeExchange.Pick(rows, 2)!.Item);
        Assert.Equal(20u, ItemChangeExchange.Pick(rows, 3)!.Item);
        Assert.Equal(20u, ItemChangeExchange.Pick(rows, 4)!.Item);
        Assert.Null(ItemChangeExchange.Pick(rows, 5));
        Assert.Null(ItemChangeExchange.Pick(rows, -1));
    }

    private sealed class FakeInventory(ulong nextId) : IInventoryRepository
    {
        private ulong _nextId = nextId;
        public readonly List<InventoryItem> Items = new();
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
        public Task RemoveItemAsync(ulong itemId) { Items.RemoveAll(i => i.Id == itemId); return Task.CompletedTask; }
        public Task UpdateItemAsync(InventoryItem item) => Task.CompletedTask;
        public Task<ulong> GetNextIdAsync() => Task.FromResult(_nextId++);
        public Task<int> RepairSlotsAsync(uint characterId) => Task.FromResult(0);
    }
}

/// <summary>
/// 1492 to 2679 AnsGuildStorageStatement_Zeny (<see cref="GuildStorageLog.BuildAnsStatementZeny2679"/>).
/// </summary>
public class GuildFundsHistory2679Tests
{
 /// <summary>Answer for a guild with no history: the same 12 bytes for the 7-day and 30-day windows.</summary>
    [Fact]
    public void NoLines_IsRetailsHeaderOnlyAnswer()
    {
        Assert.Equal(Convert.FromHexString("000006000000000000000000"),
            GuildStorageLog.BuildAnsStatementZeny2679(0, Array.Empty<GuildFundsLogEntry>()));
    }

 /// <summary>
 /// The fixed fields of a statement line: the name (with its NUL terminator), kind 3, a u64 of 0, and the NPC id
 /// at offset 67. The date is written as an f64, a u32 of 0 and a u32 high dword, and offsets 52 to 66 are
 /// pinned byte for byte. Kinds 0 and 1 leave the NPC id at 0.
 /// </summary>
    [Fact]
    public void Line_MatchesRetailsFixedFields()
    {
        var retailLine = Convert.FromHexString(
            "dd6b07560c91e640000000000c91e6404e0069006e0079006d00000000000000c0f5bc030000000050f6bc030000000003000000000000000000030000000000000000d5fefa02");
        var body = GuildStorageLog.BuildAnsStatementZeny2679(11, new[]
        {
            new GuildFundsLogEntry { WhenUtc = new DateTime(2026, 7, 13, 12, 0, 0, DateTimeKind.Utc), ActorName = "Ninym", Kind = 3, Amount = 0 },
        });

        Assert.Equal(12 + 71, body.Length);
        Assert.Equal(6u + 71, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(2)));
        Assert.Equal(11u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(6)));
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(10)));
        var line = body.AsSpan(12, 71).ToArray();

        var nameBytes = Encoding.Unicode.GetByteCount("Ninym") + 2;
        Assert.Equal(retailLine.AsSpan(16, nameBytes).ToArray(), line.AsSpan(16, nameBytes).ToArray());
        Assert.Equal(retailLine.AsSpan(52, 15).ToArray(), line.AsSpan(52, 15).ToArray());
        foreach (var l in new[] { retailLine, line })
        {
            Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(l.AsSpan(8)));
            Assert.Equal(BinaryPrimitives.ReadUInt32LittleEndian(l.AsSpan(4)), BinaryPrimitives.ReadUInt32LittleEndian(l.AsSpan(12)));
        }
        Assert.Equal(50003669u, BinaryPrimitives.ReadUInt32LittleEndian(retailLine.AsSpan(67)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(line.AsSpan(67)));
    }

 /// <summary>Kinds 0 and 1 as the client's history formatter reads them: the kind at offset 58 and the
 /// amount as a u64 at offset 59.</summary>
    [Fact]
    public void DepositAndWithdrawLines_CarryKindAndAmount()
    {
        var when = new DateTime(2026, 9, 27, 3, 0, 0, DateTimeKind.Utc);
        var body = GuildStorageLog.BuildAnsStatementZeny2679(27, new[]
        {
            new GuildFundsLogEntry { WhenUtc = when, ActorName = "Arckhon", Kind = GuildStorageLog.KindWithdraw, Amount = 200_000_000 },
            new GuildFundsLogEntry { WhenUtc = when, ActorName = "Arckhon", Kind = GuildStorageLog.KindDeposit, Amount = 9_999_990_000 },
        });

        Assert.Equal(12 + 2 * 71, body.Length);
        Assert.Equal(27u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(6)));
        var first = body.AsSpan(12, 71);
        var second = body.AsSpan(12 + 71, 71);
        Assert.Equal((byte)1, first[58]);
        Assert.Equal(200_000_000UL, BinaryPrimitives.ReadUInt64LittleEndian(first.Slice(59)));
        Assert.Equal((byte)0, second[58]);
        Assert.Equal(9_999_990_000UL, BinaryPrimitives.ReadUInt64LittleEndian(second.Slice(59)));
        Assert.Equal("Arckhon\0", Encoding.Unicode.GetString(first.Slice(16, 16)));
    }

    [Fact]
    public void PageSizeAndRetention_ComeFromConfigDef()
    {
        var config = new List<ConfigDef>
        {
            new() { type = 14, Value = "13" },
            new() { type = 15, Value = "31" },
        };
        Assert.Equal(13, GuildStorageLog.HistoryPageSize(config));
        Assert.Equal(31, GuildStorageLog.HistoryRetentionDays(config));
        
        Assert.Equal(13, GuildStorageLog.HistoryPageSize(Array.Empty<ConfigDef>()));
        Assert.Equal(0, GuildStorageLog.HistoryRetentionDays(Array.Empty<ConfigDef>()));
    }
}

/// <summary>1470 to 2635 refusal codes (<see cref="GuildMasterHandoverHandler.Precheck"/>), matching the
/// client's result messages for these causes.</summary>
public class GuildMasterHandover2635Tests
{
    [Fact]
    public void Precheck_UsesTheClientsGuildCodes()
    {
        const uint master = 41;
        Assert.Equal((ushort)7, GuildMasterHandoverHandler.Precheck(null, master));                       
        Assert.Equal((ushort)17, GuildMasterHandoverHandler.Precheck(new Guild { MasterCharId = master + 1 }, master)); 
        Assert.Equal((ushort)0, GuildMasterHandoverHandler.Precheck(new Guild { MasterCharId = master }, master));
        Assert.Equal((ushort)13, GuildMasterHandoverHandler.ResultTargetNotMember);                       
    }
}
