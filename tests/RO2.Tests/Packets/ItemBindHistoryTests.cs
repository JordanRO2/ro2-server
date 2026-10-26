using System.Buffers.Binary;
using System.Text.Json;
using RO2.Domain.Entities;
using RO2.GameData.Tables;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Services;
using Xunit;
using RO2.Inventory.Domain;

namespace RO2.Tests.Packets;

/// <summary>
/// Bind history of an item (InventoryItem.Bound). Set by the 1324 that wears a bind-on-equip item, and carried on every later record of that item, including the 2899 card-enforce answer.
/// </summary>
/// <remarks>
/// In a 2899 answer, a Bind_Type 1 card carries 2 when it was worn before and 0 when it was not. The client copies this word into the item unconditionally. After the 2327 that equips the card, the bit is 2 on the item's later bag records.
/// </remarks>
public class ItemBindHistoryTests
{
    
    
    private const string RetailBound2899 =
        "028a008a007be4118ff209000002b67b34f5a73d040ee96f54011027010136000000000000000000000200030000000000000000000000" +
        "00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
        "00000000000000000000000000000000000000000000000000000000000001";

    
    
    private const string RetailUnbound2899 =
        "028a008a007be4117ae50d00000127d21e0ea83d040ed76f54011027010134000000000000000000000000020000000000000000000000" +
        "00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
        "00000000000000000000000000000000000000000000000000000000000001";

 /// <summary>Body offset of the first record in a 2899 answer: the compact marker and length bytes, then 11 bytes of blob header.</summary>
    private const int RecordAt = 3 + 11;

    [Fact]
    public void RetailRecords_AreBoundExactlyWhenTheCardWasWorn()
    {
        var bound = Convert.FromHexString(RetailBound2899);
        var unbound = Convert.FromHexString(RetailUnbound2899);
        foreach (var body in new[] { bound, unbound })
        {
            Assert.Equal(1, BindTypeOf(BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(RecordAt + 8))));
            Assert.Equal(1, body[RecordAt + 0x0F]); 
        }

        Assert.Equal(bound[RecordAt + 0x1B], ItemPackets.RecordBindFlags(1, 0, everWorn: true));
        Assert.Equal(unbound[RecordAt + 0x1B], ItemPackets.RecordBindFlags(1, 0, everWorn: false));
        Assert.Equal(0, bound[RecordAt + 0x1C]);
    }

    [Fact]
    public void CardEnforce2899_OfAOnceWornCard_ReproducesRetail()
    {
        
        var card = new InventoryItem
        {
            Id = 0x0E043DA7F5347BB6UL, ItemId = 22310889, Count = 1, Slot = 4, StorageType = 0,
            Durability = 10000, Refine = 3, Bound = true,
        };
        var record = ItemPackets.BuildItemRecord126(card);
        
        
        record[0x1B] = ItemPackets.RecordBindFlags(BindTypeOf(card.ItemId), card.StorageType, card.Bound);

        var body = ShopItemPackets.BuildCardEnforceItem2899(0x09F28F11u, 0, 2, record);

        Assert.Equal(RetailBound2899, Convert.ToHexStringLower(body));
    }

    [Fact]
    public void Equip1324_BindsTheCard_AsTheRetail2327Does()
    {
        
        
        var before = Convert.FromHexString(
            "e7614a0ea83d040eb06f540110270104060000000000000000000000000a00000000000000000000000000000000000000000000000000" +
            "00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
            "00000000000000000000000000000000");
        var after = Convert.FromHexString(
            "e7614a0ea83d040eb06f540110270100180000000000000000000002000a00000000000000000000000000000000000000000000000000" +
            "00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
            "00000000000000000000000000000000");
        var bindType = BindTypeOf(BinaryPrimitives.ReadUInt32LittleEndian(before.AsSpan(8)));
        Assert.Equal(1, bindType);

        var card = new InventoryItem { ItemId = 22310832, StorageType = 0, Slot = 20 + 2 * 35 + 6 };
        Assert.Equal(before[0x1B], ItemPackets.RecordBindFlags(bindType, card.StorageType, card.Bound));

        var (flat, storage) = ItemSwapHandler.ClientToPos(after[0x0F], after[0x10]);
        card.Slot = (short)flat;
        card.StorageType = storage;
        Assert.True(ItemSwapHandler.BindOnWear(card, bindType));
        Assert.Equal(after[0x1B], ItemPackets.RecordBindFlags(bindType, card.StorageType, card.Bound));

        
        card.StorageType = 0;
        card.Slot = 0;
        Assert.False(ItemSwapHandler.BindOnWear(card, bindType));
        Assert.Equal(ItemPackets.RecordBoundFlag, ItemPackets.RecordBindFlags(bindType, card.StorageType, card.Bound));
    }

    [Theory]
    [InlineData(1, 1, true)]  
    [InlineData(3, 1, true)]  
    [InlineData(0, 1, false)] 
    [InlineData(2, 1, false)] 
    [InlineData(1, 0, false)] 
    [InlineData(1, 2, false)] 
    public void BindOnWear_OnlyTheWindowsThatWearAnItem_AndOnlyBindOnEquip(byte storageType, byte bindType, bool binds)
    {
        var item = new InventoryItem { StorageType = storageType };
        Assert.Equal(binds, ItemSwapHandler.BindOnWear(item, bindType));
        Assert.Equal(binds, item.Bound);
    }

    [Fact]
    public void CopyInstanceState_KeepsTheBindHistory_OnASplitOffStack()
    {
        var worn = new InventoryItem { Bound = true, Refine = 3 };
        var split = new InventoryItem { Count = 1 }.CopyInstanceStateFrom(worn);
        Assert.True(split.Bound);
    }

    internal static byte BindTypeOf(uint templateId)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(DataFile("ItemInfo.json")));
        foreach (var row in doc.RootElement.EnumerateArray())
            if (row.GetProperty("ID").GetUInt32() == templateId)
                return row.GetProperty("Bind_Type").GetByte();
        throw new InvalidOperationException($"template {templateId} is not in data/ItemInfo.json");
    }

 /// <summary>Walks up from the test output path to the repository's data directory.</summary>
    internal static string DataFile(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "data", name);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("no ancestor of " + AppContext.BaseDirectory + " holds data/" + name);
    }
}

/// <summary>
/// The owned-vehicle list (2522 OnAllVehicleList) and the reins item that adds to it (1323 on an Item_Type 11 item), kept per character by VehicleRepository. Grouped with the bind history because both are per-item state left behind by item use or wearing.
/// </summary>
/// <remarks>
/// An expired vehicle is renewed to now plus the reins item's duration, and each further reins adds its duration to the current expiry. Expiry is stored as a float32 OLE date.
/// </remarks>
public class VehicleOwnership2522Tests
{
    private static readonly JsonSerializerOptions TableOptions = new() { PropertyNameCaseInsensitive = true };

    
    private const string Before =
        "e30000000f00a0985d050000000000000000010100b0bf5d051cddf39f067ee640010400e0345e051cddf39f467ce640010400f05b5e05" +
        "1cddf39f467ce64001040000835e0500000040887ae64001040010aa5e0500000000c87fe640010400606d5f051cddf3df0808e5400104" +
        "0080bb5f051cddf35f947fe640010400b03060051cddf3df3d0ee540010400e0a560051cddf31f27a3e440010400f0cc60050000000000" +
        "00000001010000f460050000000000000000010100101b61051cddf3dfe4e4e440010400c0c862051cddf35f1384e640010400108c6305" +
        "1cddf39f467ce640010400";

    
    private const string AfterFirst =
        "e30000000f00a0985d050000000000000000010100b0bf5d051cddf39f067ee640010400e0345e051cddf39f467ce640010400f05b5e05" +
        "1cddf39f467ce64001040000835e0500000040887ae64001040010aa5e0500000000c87fe640010400606d5f051cddf3df0808e5400104" +
        "0080bb5f051cddf35f947fe640010400b0306005000000609292e640010200e0a560051cddf31f27a3e440010400f0cc60050000000000" +
        "00000001010000f460050000000000000000010100101b61051cddf3dfe4e4e440010400c0c862051cddf35f1384e640010400108c6305" +
        "1cddf39f467ce640010400";

    
    private const string AfterSecond =
        "e30000000f00a0985d050000000000000000010100b0bf5d051cddf39f067ee640010400e0345e051cddf39f467ce640010400f05b5e05" +
        "1cddf39f467ce64001040000835e0500000040887ae64001040010aa5e0500000000c87fe640010400606d5f051cddf3df0808e5400104" +
        "0080bb5f051cddf35f947fe640010400b0306005000000603293e640010200e0a560051cddf31f27a3e440010400f0cc60050000000000" +
        "00000001010000f460050000000000000000010100101b61051cddf3dfe4e4e440010400c0c862051cddf35f1384e640010400108c6305" +
        "1cddf39f467ce640010400";

    [Fact]
    public void Reins_OnAnExpiredVehicle_StartsFromNow_AndReproducesRetailsList()
    {
        var now = new DateTime(2026, 7, 20, 13, 44, 50, 184).ToOADate();
        Assert.True(Resolve(22250031, out var ridingId, out var days));
        Assert.Equal(90190000u, ridingId);
        Assert.Equal(5u, days);

        var owned = VehicleRules.Grant(Parse(Before), ridingId, days, now);

        Assert.Equal(AfterFirst, Convert.ToHexStringLower(VehiclePackets.BuildAllVehicleList2522(owned, now)));
    }

    [Fact]
    public void Reins_OnAVehicleStillRunning_AddsTheDaysToItsExpiry()
    {
        var now = new DateTime(2026, 7, 20, 13, 44, 50, 490).ToOADate();

        var owned = VehicleRules.Grant(Parse(AfterFirst), 90190000, 5, now);

        Assert.Equal(AfterSecond, Convert.ToHexStringLower(VehiclePackets.BuildAllVehicleList2522(owned, now)));
    }

    [Fact]
    public void Reins_ForAFirstVehicle_ReproducesRetailsOneRecordList()
    {
        
        var now = new DateTime(2026, 7, 3, 21, 14, 7, 613).ToOADate();
        Assert.True(Resolve(60000995, out var ridingId, out var days));
        Assert.Equal((90280000u, (uint?)14u), (ridingId, days));

        var owned = VehicleRules.Grant(Array.Empty<OwnedVehicle>(), ridingId, days, now);

        Assert.Equal("11000000010040906105000000609c91e640010200",
            Convert.ToHexStringLower(VehiclePackets.BuildAllVehicleList2522(owned, now)));
    }

    [Fact]
    public void PermanentReins_GivesExpiryZero_AndKeepsAPermanentVehicle()
    {
        Assert.True(Resolve(22250060, out var ridingId, out var days)); 
        Assert.Equal(90190000u, ridingId);
        Assert.Null(days);

        var now = new DateTime(2026, 7, 20, 13, 44, 50).ToOADate();
        var permanent = VehicleRules.Grant(Parse(Before), ridingId, days, now);
        Assert.Equal(0, permanent.Single(v => v.RidingId == 90190000).ExpiryOle);

        
        var again = VehicleRules.Grant(permanent, 90190000, 5, now);
        Assert.Equal(0, again.Single(v => v.RidingId == 90190000).ExpiryOle);
    }

    [Fact]
    public void UnmappedReins_IsNotResolved()
    {
        
        Assert.False(Resolve(12721019, out _, out _));
        Assert.False(Resolve(60000365, out _, out _));
    }

    [Theory]
    [InlineData(0.0, 1)]
    [InlineData(46300.0, 2)]
    [InlineData(43121.933587962965, 4)]
    public void State_FollowsTheExpiry(double expiry, byte state)
        => Assert.Equal(state, VehiclePackets.StateOf(expiry, 46223.5));

    [Fact]
    public void NoVehicle_IsRetailsEmptyForm()
    {
        Assert.Equal("020000000000", Convert.ToHexStringLower(
            VehiclePackets.BuildAllVehicleList2522(Array.Empty<OwnedVehicle>(), 46223.5)));
        Assert.Equal("020000000000", Convert.ToHexStringLower(VehiclePackets.BuildVehicleQuickSlot2523Empty()));
    }

    private static List<OwnedVehicle> Parse(string hex)
    {
        var b = Convert.FromHexString(hex);
        Assert.Equal((uint)(b.Length - 4), BinaryPrimitives.ReadUInt32LittleEndian(b));
        int n = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(4));
        var list = new List<OwnedVehicle>();
        for (int k = 0; k < n; k++)
        {
            var r = b.AsSpan(6 + VehiclePackets.RecordLength * k, VehiclePackets.RecordLength);
            list.Add(new OwnedVehicle
            {
                RidingId = BinaryPrimitives.ReadUInt32LittleEndian(r),
                ExpiryOle = BinaryPrimitives.ReadDoubleLittleEndian(r.Slice(4)),
            });
        }
        return list;
    }

    private static bool Resolve(uint itemId, out uint ridingId, out uint? days)
        => VehicleRules.TryResolveReins(Table<RidingDurationGroup>("RidingDurationGroup.json"),
            Table<RidingInfo>("RidingInfo.json"), Table<ItemCashInfo>("ItemCashInfo.json"), itemId, out ridingId, out days);

    private static List<T> Table<T>(string file)
        => JsonSerializer.Deserialize<List<T>>(File.ReadAllBytes(ItemBindHistoryTests.DataFile(file)), TableOptions)!;
}
