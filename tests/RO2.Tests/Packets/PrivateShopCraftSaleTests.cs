using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Byte-exact checks for the private-shop craft sale (1483, answered by 2329, 2333 and 2660 to the buyer and by 2654
/// to the seller), the buyer's craft list (2658) and the refine-shop owner's report (3005).
/// </summary>
public class PrivateShopCraftSaleTests
{
    [Fact]
    public void BuyPrivateShopCraft2660_ReproducesRetailFrame() =>
        
        Assert.Equal(Convert.FromHexString("0000480665e579500200000059dbfe00"),
            PrivateShopCraftPackets.BuildBuyPrivateShopCraft2660(1608, 0x2_5079E565L, 16702297));

    [Fact]
    public void BuyPrivateShopCraft2660_ReproducesTheCraft103Answer() =>
        
        Assert.Equal(Convert.FromHexString("000067006292e30300000000cacee901"),
            PrivateShopCraftPackets.BuildBuyPrivateShopCraft2660(103, 0x03E39262L, 32100042));

    [Theory]
    [InlineData((ushort)1608, 0x1_B925CA9FL, "480611d94f099fca25b90100000000000000")]
    [InlineData((ushort)1608, 0x1_B925CAA0L, "480611d94f09a0ca25b90100000000000000")]
    [InlineData((ushort)1618, 0x1_B925CAA1L, "520611d94f09a1ca25b90100000000000000")]
    public void CraftSaleShopItemBuy2654_ReproducesTheSellerFrames(ushort craftId, long sellerZeny, string retailHex) =>
        
        Assert.Equal(Convert.FromHexString(retailHex),
            PrivateShopCraftPackets.BuildCraftSaleShopItemBuy2654(craftId, 0x094FD911u, sellerZeny));

    [Fact]
    public void BuyerCraftList2658_ReproducesRetailFrameEntryForEntry()
    {
        
        var retail = Convert.FromHexString(
            "02ec000000e4000000000000000000000000000000000000000000000000000000000000000000000000000000000013" +
            "004806010000000000000042060100000000000000520601000000000000004c06010000000000000041060100000000" +
            "00000051060100000000000000450601000000000000007b000100000000000000430601000000000000004606010000" +
            "00000000004f06010000000000000050060100000000000000870501000000000000004d0601000000000000004e0601" +
            "0000000000000054060100000000000000390601000000000000004b06010000000000000049060100000000000000");
        var blob = retail[3..];   
        var title = blob[2..(2 + PrivateShopRegistry.NameBytes)];
        int count = BitConverter.ToUInt16(blob, 44);
        var offers = Enumerable.Range(0, count)
            .Select(i => new PrivateShopCraftOffer(
                BitConverter.ToUInt16(blob, 46 + 10 * i), BitConverter.ToUInt64(blob, 48 + 10 * i)))
            .ToList();
        Assert.Equal(19, offers.Count);
        Assert.Equal(retail, PrivateShopCraftPackets.BuildBuyerCraftList2658(title, offers));
    }

    [Fact]
    public void BuyerCraftList2658_ReproducesTheNoCraftForm()
    {
        
        var retail = Convert.FromHexString(
            "012e000048006f006e0065004400650070006f0074000000000000000000000000000000000000000000000000000000");
        var title = retail.AsSpan(4, PrivateShopRegistry.NameBytes).ToArray();
        Assert.Equal(retail, PrivateShopCraftPackets.BuildBuyerCraftList2658(title, new List<PrivateShopCraftOffer>()));
    }

    [Theory]
    [InlineData(1u, (byte)17, (byte)18, "0125000096e41156510a00000100000011124ed9fe000007004100720063006b0068006f006e00")]
    [InlineData(0u, (byte)17, (byte)18, "0125000096e41156510a00000000000011124ed9fe000007004100720063006b0068006f006e00")]
    [InlineData(0u, (byte)18, (byte)19, "0125000096e41156510a00000000000012134ed9fe000007004100720063006b0068006f006e00")]
    [InlineData(0u, (byte)19, (byte)20, "0125000096e41156510a00000000000013144ed9fe000007004100720063006b0068006f006e00")]
    public void RefineItemToSeller3005_ReproducesTheSellerFrames(uint outcome, byte oldGrade, byte newGrade, string retailHex) =>
        
        Assert.Equal(Convert.FromHexString(retailHex),
            RefineToSellerPackets.BuildRefineItemToSeller3005(0x0A515611u, outcome, oldGrade, newGrade, 16701774, "Arckhon"));

    [Fact]
    public async Task CraftMaterials_TakeTheLowerSlotFirstAndSend2329UseSource1()
    {
        
        
        var inventory = new FakeInventory(
            new InventoryItem { Id = 0x0E043DA802065615, ItemId = 32100041, Count = 1, Slot = 112 },
            new InventoryItem { Id = 0x0E043DA8020A1E19, ItemId = 32100041, Count = 99, Slot = 113 });
        var bag = await inventory.GetByCharacterAsync(0);
        var touched = await CraftItemHandler.TakeMaterialAsync(inventory, bag, 32100041, 2);

        var frames = touched.Select(s =>
        {
            var (bagId, slot) = ItemWorkbench.ClientBagSlot(s);
            return Convert.ToHexString(CombatPackets.BuildUseItem2329(0x0BBA2211u, slot, bagId, s.Id,
                (byte)s.Count, useSource: 1)).ToLowerInvariant();
        }).ToArray();
        Assert.Equal(new[]
        {
            "01161600a6e21122ba0b0000160415560602a83d040e0001",
            "01161600a6e21122ba0b00001704191e0a02a83d040e6201",
        }, frames);
        Assert.Single(inventory.Items);   
        Assert.Equal(98, inventory.Items[0].Count);
    }

    [Fact]
    public void Viewer_IsOnlyTheBuyerWhoOpenedTheShopUntilItEnds()
    {
        var owner = new PlayerState { SessionId = 1, CharacterId = 10, EntityId = 0x094F8011u };
        var buyer = new PlayerState { SessionId = 2, CharacterId = 20, EntityId = 0x094FD911u };
        var other = new PlayerState { SessionId = 3, CharacterId = 30, EntityId = 0x0951B511u };
        PrivateShopRegistry.SetName(owner, Encoding.Unicode.GetBytes("shop"));   
        PrivateShopRegistry.AddViewer(owner, buyer);                             
        Assert.True(PrivateShopRegistry.IsViewer(owner, buyer));
        Assert.False(PrivateShopRegistry.IsViewer(owner, other));
        Assert.False(PrivateShopRegistry.IsViewer(owner, owner));
        PrivateShopRegistry.TakeViewers(owner);                                  
        Assert.False(PrivateShopRegistry.IsViewer(owner, buyer));
    }

    private sealed class FakeInventory : IInventoryRepository
    {
        public readonly List<InventoryItem> Items;
        public FakeInventory(params InventoryItem[] items) => Items = items.ToList();
        public Task<IReadOnlyList<InventoryItem>> GetByCharacterAsync(uint characterId) =>
            Task.FromResult<IReadOnlyList<InventoryItem>>(Items.ToList());
        public Task<IReadOnlyList<InventoryItem>> GetEquipmentAsync(uint characterId) =>
            Task.FromResult<IReadOnlyList<InventoryItem>>(Array.Empty<InventoryItem>());
        public Task<InventoryItem?> GetByIdAsync(ulong id) => Task.FromResult(Items.FirstOrDefault(i => i.Id == id));
        public Task AddItemAsync(InventoryItem item) { Items.Add(item); return Task.CompletedTask; }
        public Task RemoveItemAsync(ulong itemId) { Items.RemoveAll(i => i.Id == itemId); return Task.CompletedTask; }
        public Task UpdateItemAsync(InventoryItem item) => Task.CompletedTask;
        public Task<ulong> GetNextIdAsync() => Task.FromResult(Items.Count == 0 ? 1UL : Items.Max(i => i.Id) + 1);
        public Task<int> RepairSlotsAsync(uint characterId) => Task.FromResult(0);
    }
}
