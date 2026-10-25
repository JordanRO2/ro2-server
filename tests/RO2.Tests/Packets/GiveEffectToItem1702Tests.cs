using RO2.Economy.Domain;
using System.Buffers.Binary;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Network.Packets;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>Resolves the repository's data/ directory by walking up from the test binaries (CI runs on Linux).</summary>
internal static class ItemLookTestData
{
    public static string File(params string[] parts)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !System.IO.File.Exists(Path.Combine(root.FullName, "data", "ItemInfo.json")))
            root = root.Parent;
        if (root == null)
            throw new DirectoryNotFoundException("no ancestor of " + AppContext.BaseDirectory + " holds data/ItemInfo.json");
        return Path.Combine(new[] { root.FullName, "data" }.Concat(parts).ToArray());
    }
}

/// <summary>
/// 1702 ReqGiveEffectToItem and 3039 AnsGiveEffectToItem (<see cref="GiveEffectToItemHandler"/>), and where
/// the effect is stored (record+113, VEC28 +23). The handler sends, in order, 2435 (the fee), 2329 (the effect
/// item used up, useSource 3) and the 3039 echo.
/// </summary>
public class GiveEffectToItem1702Tests
{
    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    [Theory]
    
    [InlineData("138a0100cc000000581947f8a73d040e9d3df30da83d040e", "0000cc000000581947f8a73d040e9d3df30da83d040e")]
    
    [InlineData("138901003f000000e31c4aeda73d040ea9d1870ea83d040e", "00003f000000e31c4aeda73d040ea9d1870ea83d040e")]
    public void The3039_EchoesTheRequest_AsRetail(string requestHex, string answerHex)
    {
        var req = Convert.FromHexString(requestHex);
        var answer = GiveEffectToItemHandler.Build3039(GiveEffectToItemHandler.ResultApplied,
            BinaryPrimitives.ReadUInt32LittleEndian(req.AsSpan(4)),
            BinaryPrimitives.ReadUInt64LittleEndian(req.AsSpan(8)),
            BinaryPrimitives.ReadUInt64LittleEndian(req.AsSpan(16)));
        Assert.Equal(answerHex, Hex(answer));
    }

    [Fact]
    public void AShortWallet_Is16209_OverTheSameEcho()
    {
        
        var answer = GiveEffectToItemHandler.Build3039(GiveEffectToItemHandler.ResultNoZeny, 204, 0x0E043DA7F8471958, 0x0E043DA80DF33D9D);
        Assert.Equal("513fcc000000581947f8a73d040e9d3df30da83d040e", Hex(answer));
    }

    [Fact]
    public void TheFee_Is1000PerTargetItemLevel_AsRetailCharged()
    {
        
        
        var levels = GiveEffectToItemHandler.LoadItemLevels(ItemLookTestData.File("ItemInfo.json"));
        Assert.Equal(105, levels[16702360]);
        Assert.Equal(105_000, GiveEffectToItemHandler.Fee(levels[16702360]));
        Assert.Equal(40_325_781, 40_430_781 - GiveEffectToItemHandler.Fee(105));
        Assert.Equal(7_503_036_865, 7_503_141_865 - GiveEffectToItemHandler.Fee(105));
        
        var retail2435 = Convert.FromHexString("01141400c6e31177540a955267020000000044170000");
        Assert.Equal(40_325_781UL, BinaryPrimitives.ReadUInt64LittleEndian(retail2435.AsSpan(2 + 8))); 
    }

    [Fact]
    public async Task EveryFxTarget_HasAnItemLevel()
    {
        
        var levels = GiveEffectToItemHandler.LoadItemLevels(ItemLookTestData.File("ItemInfo.json"));
        var dir = Path.Combine(Path.GetTempPath(), "ro2-fxenchant-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.Copy(ItemLookTestData.File("FXenchant_INFO.json"), Path.Combine(dir, "FXenchant_INFO.json"));
            var store = new GameDataStore();
            await store.LoadAsync(dir);
            Assert.Equal(234, store.FXenchantINFO.Count);
            foreach (var row in store.FXenchantINFO)
                Assert.Contains(levels[row.Taget_Item_ID], new[] { 0, 85, 90, 95, 100, 105 });
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    
    [InlineData(0x0A547711u, 50001147u, (short)76, 1, 0x0E043DA80DF33D9DUL, "01161600a6e21177540a000015039d3df30da83d040e0003")]
    
    [InlineData(0x00192211u, 50001145u, (short)95, 1, 0x0E043DA80E87D1A9UL, "01161600a6e21122190000000504a9d1870ea83d040e0003")]
    public void TheEffectItem2329_IsRetails(uint handle, uint template, short flatSlot, int count, ulong uid, string retailHex)
    {
        var effectItem = new InventoryItem { Id = uid, ItemId = template, Count = count, StorageType = 0, Slot = flatSlot };
        var (bag, slot) = ItemWorkbench.ClientBagSlot(effectItem);
        var body = CombatPackets.BuildUseItem2329(handle, slot, bag, uid, (byte)(count - 1), GiveEffectToItemHandler.UseSourceFxEnchant);
        Assert.Equal(retailHex, Hex(body));
    }

    [Fact]
    public void TheEffect_GoesToRecord113_AndToTheWornRecordAt23()
    {
        
        var item = new InventoryItem { Id = 0x0E043DA7F8471958, ItemId = 16702360, Count = 1, StorageType = 1, Slot = 10, FxEnchantIndex = 204 };
        var record = WorldEntryService.BuildItemRecord126(item);
        Assert.Equal(126, record.Length);
        Assert.Equal(204u, BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(113)));
        Assert.All(record[117..], b => Assert.Equal((byte)0, b));

        using var pw = new PacketWriter(28);
        EquipViewNotifyPackets.WriteEquipRecord28(pw, item);
        var worn = pw.ToArray();
        Assert.Equal(28, worn.Length);
        Assert.Equal(16702360u, BinaryPrimitives.ReadUInt32LittleEndian(worn.AsSpan(1)));
        Assert.Equal(204u, BinaryPrimitives.ReadUInt32LittleEndian(worn.AsSpan(23)));
    }

    [Fact]
    public void AnItemWithNoLook_KeepsTheOldRecordBytes()
    {
        
        var record = WorldEntryService.BuildItemRecord126(new InventoryItem { Id = 77, ItemId = 11100001, Count = 1, StorageType = 0, Slot = 1 });
        foreach (var off in new[] { 0x11, 0x13, 0x17, 0x65, 0x69, 0x6D, 0x71 })
            Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(off)) & (off is 0x11 or 0x13 ? 0xFFFFu : 0xFFFFFFFFu));
    }

    [Fact]
    public void CopyInstanceState_CarriesTheLookAndTheEnchant()
    {
        var source = new InventoryItem { DyeColor = 0xFFFE8C06, DyeColor2 = 0xFFB8A878, DyeColor3 = 0xFF550200, FxEnchantIndex = 169,
            EnchantReinforceId = 30003, EnchantSeconds = 290 };
        var copy = new InventoryItem { Count = 1 }.CopyInstanceStateFrom(source);
        Assert.Equal((0xFFFE8C06u, 0xFFB8A878u, 0xFF550200u, 169u, (ushort)30003, (ushort)290),
            (copy.DyeColor, copy.DyeColor2, copy.DyeColor3, copy.FxEnchantIndex, copy.EnchantReinforceId, copy.EnchantSeconds));
    }
}

/// <summary>
/// 1575 ReqUseDyeItem and 2835 AnsUseDyeItem (<see cref="UseDyeItemHandler"/>, <see cref="DyePackets"/>),
/// and the dye colours (<see cref="DyeColorCatalog"/>, data/dye/ColorPicker.json). Dye and the FX enchant
/// share this file because both write per-item looks into the same item record and worn record.
/// </summary>
public class UseDyeItem1575Tests
{
    private const uint Owner = 9;
    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    private static readonly Lazy<DyeColorCatalog> Catalog = new(() => DyeColorCatalog.Load(ItemLookTestData.File("dye", "ColorPicker.json")));
    private static readonly Lazy<Task<GameDataStore>> Store = new(LoadAsync);

    private static async Task<GameDataStore> LoadAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ro2-dye-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            
            File.Copy(ItemLookTestData.File("ColorPickerInfo.json"), Path.Combine(dir, "ColorPickerInfo.json"));
            await File.WriteAllTextAsync(Path.Combine(dir, "ItemInfo.json"), """
                [{"ID":70001638,"Item_Type":8},{"ID":70001634,"Item_Type":8},{"ID":30000278,"Item_Type":8},
                 {"ID":60000209,"Item_Type":8},{"ID":11100001,"Item_Type":1}]
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

    public static TheoryData<string, uint, string, string> RetailDyes => new()
    {
        
        
        { "d414c84e 15:12:53", 70001638, "041470283603a83d040e0133546884ffa73d040e6854010001",
          "000000041470283603a83d040e068cfeff0133546884ffa73d040e01" },
        
        { "02d06ec2 02:14:40", 70001634, "0207afcf8f0da83d040e0010513df908a83d040e9b7b010000",
          "0000030207afcf8f0da83d040effffffff0010513df908a83d040e00" },
        
        
        { "f560bfac 16:36:59", 30000278, "01428b32100ea83d040e00108e9fb00aa83d040e979d010000",
          "00000001428b32100ea83d040e78a8b8ff00108e9fb00aa83d040e00" },
    };

    [Theory]
    [MemberData(nameof(RetailDyes))]
    public async Task ADye_ResolvesRetailsColour_AndThe2835IsRetails(string frame, uint dyeTemplate, string requestHex, string answerHex)
    {
        var store = await Store.Value;
        var req = Convert.FromHexString(requestHex);
        var answer = Convert.FromHexString(answerHex);
        var colour = Catalog.Value.Resolve(store, dyeTemplate, DyePackets.Pick(req));
        Assert.True(colour.HasValue, frame);
        Assert.Equal(BinaryPrimitives.ReadUInt32LittleEndian(answer.AsSpan(13)), colour.Value);
        Assert.Equal(answerHex, Hex(DyePackets.Build2835Success(answer[2], req, colour.Value)));
    }

    [Fact]
    public void ARefusal_IsRetailsEchoForm()
    {
        
        var req = Convert.FromHexString("040e419eec0ea83d040e000ede57c50ea83d040e648f010000");
        Assert.Equal("010000040e419eec0ea83d040e00000000000ede57c50ea83d040e00",
            Hex(DyePackets.Build2835Refusal(DyePackets.ResultRefused, req)));
    }

    [Fact]
    public async Task APaletteNumberOutsideSelectNum_OrAPointOffTheBitmap_GivesNoColour()
    {
        var store = await Store.Value;
        Assert.Null(Catalog.Value.Resolve(store, 60000209, 0x00049d97)); 
        Assert.Null(Catalog.Value.Resolve(store, 60000209, 0x00009d97)); 
        Assert.Null(Catalog.Value.Resolve(store, 60000209, 0x0001F000)); 
        Assert.NotNull(Catalog.Value.Resolve(store, 60000209, 0x0003EFDB)); 
        Assert.Null(Catalog.Value.Resolve(store, 11100001, 0x00015468)); 
    }

    [Fact]
    public async Task EveryDyeTemplate_HasABitmapInTheColourFile()
    {
        
        var store = await Store.Value;
        Assert.Equal(96, store.ColorPickerInfo.Count);
        foreach (var row in store.ColorPickerInfo)
            for (uint n = 1; n <= row.Select_Num; n++)
                Assert.NotNull(Catalog.Value.Resolve(store, row.Group_ItemID, (n << 16) | (100u << 8) | 100u));
    }

    [Fact]
    public async Task Check_RefusesWhatIsNotAnOwnedDyeOnAnOwnedItem()
    {
        var store = await Store.Value;
        var catalog = Catalog.Value;
        var dye = new InventoryItem { Id = 1, CharacterId = Owner, ItemId = 70001638, Count = 2, StorageType = 0, Slot = 3 };
        var sword = new InventoryItem { Id = 2, CharacterId = Owner, ItemId = 11100001, Count = 1, StorageType = 1, Slot = 6 };
        const uint pick = 0x00015468;
        Assert.Equal(0xFFFE8C06u, UseDyeItemHandler.Check(store, catalog, Owner, dye, sword, pick, 0, out _));
        Assert.Null(UseDyeItemHandler.Check(store, catalog, Owner + 1, dye, sword, pick, 0, out _));     
        Assert.Null(UseDyeItemHandler.Check(store, catalog, Owner, sword, dye, pick, 0, out _));         
        Assert.Null(UseDyeItemHandler.Check(store, catalog, Owner, dye, dye, pick, 0, out _));           
        Assert.Null(UseDyeItemHandler.Check(store, catalog, Owner, dye, null, pick, 0, out _));          
        Assert.Null(UseDyeItemHandler.Check(store, catalog, Owner, dye, sword, pick, 3, out _));         
        Assert.Null(UseDyeItemHandler.Check(store, null, Owner, dye, sword, pick, 0, out _));            
        var banked = new InventoryItem { Id = 3, CharacterId = Owner, ItemId = 11100001, Count = 1, StorageType = 2 };
        Assert.Null(UseDyeItemHandler.Check(store, catalog, Owner, dye, banked, pick, 0, out _));        
    }

    [Fact]
    public void TheThreeDyeSlots_AreRecord17_101_105_AndWornRecord5_11_15()
    {
        var item = new InventoryItem { Id = 0x0E043DA884FF5433, ItemId = 11100001, Count = 1, StorageType = 1, Slot = 6 };
        UseDyeItemHandler.SetDyeSlot(item, 0, 0xFFFE8C06);
        UseDyeItemHandler.SetDyeSlot(item, 1, 0xFFB8A878);
        UseDyeItemHandler.SetDyeSlot(item, 2, 0xFF550200);
        var record = WorldEntryService.BuildItemRecord126(item);
        Assert.Equal(0xFFFE8C06u, BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(0x17)));
        Assert.Equal(0xFFB8A878u, BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(0x65)));
        Assert.Equal(0xFF550200u, BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(0x69)));

        using var pw = new PacketWriter(28);
        EquipViewNotifyPackets.WriteEquipRecord28(pw, item);
        var worn = pw.ToArray();
        Assert.Equal(0xFFFE8C06u, BinaryPrimitives.ReadUInt32LittleEndian(worn.AsSpan(5)));
        Assert.Equal(0xFFB8A878u, BinaryPrimitives.ReadUInt32LittleEndian(worn.AsSpan(11)));
        Assert.Equal(0xFF550200u, BinaryPrimitives.ReadUInt32LittleEndian(worn.AsSpan(15)));
        Assert.Throws<ArgumentOutOfRangeException>(() => UseDyeItemHandler.SetDyeSlot(item, 3, 1));
    }
}

/// <summary>
/// Dye colours persist to Mongo (<see cref="DyeColorBson"/>). Every colour has alpha 0xFF, so its value is at
/// least 2^31; the default uint serializer's checked Int32 threw on it, failing the 1575 save before its 2835.
/// The same failure would hit an auction article, a mail or the guild vault holding a dyed item. The packet
/// tests never serialize to BSON, which is why this was not caught.
/// </summary>
public class DyeColorBsonTests
{
    
    
    private const uint Premium7 = 0xFFFE8C06, MiddlePixel = 0xFFB8A878, Dark = 0xFF550200;

    [Fact]
    public void ADyedItemRow_SavesAsInt64_AndLoadsWithItsThreeColours()
    {
        DyeColorBson.Register();
        var item = new InventoryItem { Id = 0x0E043DA884FF5433, CharacterId = 9, ItemId = 11100001, StorageType = 1, Slot = 6 };
        UseDyeItemHandler.SetDyeSlot(item, 0, Premium7);
        UseDyeItemHandler.SetDyeSlot(item, 1, MiddlePixel);
        UseDyeItemHandler.SetDyeSlot(item, 2, Dark);

        var doc = item.ToBsonDocument();
        Assert.Equal(BsonType.Int64, doc[nameof(InventoryItem.DyeColor)].BsonType);
        Assert.Equal((long)Premium7, doc[nameof(InventoryItem.DyeColor)].AsInt64);
        Assert.Equal((long)MiddlePixel, doc[nameof(InventoryItem.DyeColor2)].AsInt64);
        Assert.Equal((long)Dark, doc[nameof(InventoryItem.DyeColor3)].AsInt64);

        var back = BsonSerializer.Deserialize<InventoryItem>(doc);
        Assert.Equal((Premium7, MiddlePixel, Dark), (back.DyeColor, back.DyeColor2, back.DyeColor3));
    }

    [Fact]
    public void ARowSavedBeforeTheMapping_StillLoads_AndAnUnknownFieldIsStillIgnored()
    {
        DyeColorBson.Register();
        
        
        var doc = new InventoryItem { Id = 5, CharacterId = 9, ItemId = 11100001 }.ToBsonDocument();
        doc[nameof(InventoryItem.DyeColor)] = new BsonInt32(0);
        doc.Remove(nameof(InventoryItem.DyeColor2));
        doc.Remove(nameof(InventoryItem.DyeColor3));
        doc["AFieldOfALaterBuild"] = 1;

        var back = BsonSerializer.Deserialize<InventoryItem>(doc);
        Assert.Equal((5ul, 11100001u, 0u, 0u, 0u), (back.Id, back.ItemId, back.DyeColor, back.DyeColor2, back.DyeColor3));
    }

    [Fact]
    public void TheAuctionMailAndVaultSnapshots_KeepADyedItemsColour()
    {
        DyeColorBson.Register();
        var article = BsonSerializer.Deserialize<AuctionArticle>(new AuctionArticle { Id = 1, ItemId = 11100001, DyeColor = Premium7 }.ToBsonDocument());
        Assert.Equal(Premium7, article.DyeColor);

        var mail = BsonSerializer.Deserialize<MailMessage>(new MailMessage { Id = 1, AttachedItemId = 11100001, AttachedItemDyeColor = Premium7 }.ToBsonDocument());
        Assert.Equal(Premium7, mail.AttachedItemDyeColor);

        
        var guild = new Guild { Id = 1, Name = "g" };
        guild.Storage.Add(new GuildStorageItem { Uid = 2, ItemId = 11100001, DyeColor = Premium7 });
        var back = BsonSerializer.Deserialize<Guild>(guild.ToBsonDocument());
        Assert.Equal(Premium7, Assert.Single(back.Storage).DyeColor);
    }

    [Fact]
    public void EveryStoredType_HasItsDyeMembersMappedToInt64()
    {
        DyeColorBson.Register();
        foreach (var type in DyeColorBson.StoredTypes)
        {
            var members = DyeColorBson.DyeMembers(BsonClassMap.LookupClassMap(type)).ToList();
            Assert.NotEmpty(members); 
            Assert.All(members, m => Assert.True(DyeColorBson.IsInt64(m), type.Name + "." + m.MemberName));
        }
        Assert.Equal(3, DyeColorBson.DyeMembers(BsonClassMap.LookupClassMap(typeof(InventoryItem))).Count());
        DyeColorBson.Register(); 
    }
}
