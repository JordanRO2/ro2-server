using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using RO2.Application.Interfaces;
using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;
using RO2.Inventory.Domain;

namespace RO2.Tests.Packets;

/// <summary>
/// Private-shop item sale: request 1482 ReqBuyPrivateShopItem and its answers 2659 (buyer), 2662 (the shop's watchers),
/// 2655 and 2656 (seller). Byte-exact checks for 1482, 2659 and 2662 are in PrivateShopItemSaleReauditTests. The 2655
/// and 2656 layouts follow the client's readers. The 153-byte list entry is checked against 2657 listings.
/// </summary>
public class PrivateShopItemSaleTests
{
 /// <summary>Expected 2657 with one item (index 0, item , count 1, price 2008) and title "asd".</summary>
    private static readonly byte[] Retail2657 = Convert.FromHexString(
        "02d300000061007300640000000000000000000000000000000000000000000000000000000000000000000000000001" +
        "000000000000000000000000000000b053010000010001007e9b610ea83d040ed8070000000000007e9b610ea83d040e" +
        "00b05301000001040c000000000000000000000000000000000000000000000000000000000000000000000000000000" +
        "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
        "00000000000000000000000000000000000000000000");

    private const uint BuyerHandle = 0x094FD911u;
    private const uint OwnerHandle = 0x094F8011u;

 /// <summary>The first two 153-byte entries and the 27-byte head of a third entry, from a 2657 with three items.</summary>
    private static readonly string[] Dump2657Entries =
    [
        "00bd8a93030000140014009c7e9c0ea83d040eef86f411000000009c7e9c0ea83d040ebd8a9303000014040300000000" +
        "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
        "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
        "000000000000000000",
        "01bd8a9303000021002100f0517a0ea83d040e992de01d00000000f0517a0ea83d040ebd8a9303000021020700000000" +
        "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
        "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
        "000000000000000000",
        "02866f5401000006000600874a05dca73d040efda3cb0100000000",
    ];

    [Fact]
    public void ItemListUpdate2662_IsTheRetail2657WithNoTitleAndTheBuyerAtBlob46()
    {
        
        
        
        
        var blob = Retail2657.AsSpan(3);
        var entry = new PrivateShopItemListPackets.Entry(0, 0x0153B000u, 0, 0, 1, 0x0E043DA80E619B7EUL, 2008,
            blob.Slice(58 + 27, 126).ToArray());

        var expected = (byte[])Retail2657.Clone();
        Array.Clear(expected, 3 + 2, PrivateShopRegistry.NameBytes);
        BitConverter.GetBytes(BuyerHandle).CopyTo(expected, 3 + 46);
        Assert.Equal(expected, PrivateShopItemListPackets.BuildItemListUpdate2662(BuyerHandle, new[] { entry }));
    }

    [Fact]
    public void ListEntry_ReproducesTheSecondRetail2657Item()
    {
        
        var retailEntry27 = Convert.FromHexString("0100b053010000010001007e9b610ea83d040e0100000000000000");
        var record = Retail2657.AsSpan(3 + 58 + 27, 126).ToArray();
        using var w = new RO2.Network.Packets.PacketWriter(153);
        PrivateShopItemListPackets.WriteEntry(w, new PrivateShopItemListPackets.Entry(1, 0x0153B000u, 0, 0, 1,
            0x0E043DA80E619B7EUL, 1, record));
        var built = w.ToArray();
        Assert.Equal(retailEntry27, built[..27]);
        Assert.Equal(record, built[27..]);
    }

    [Fact]
    public void ListEntry_ReproducesTheDumpThreeItem2657Entries()
    {
        
        for (int i = 0; i < Dump2657Entries.Length; i++)
        {
            var retail = Convert.FromHexString(Dump2657Entries[i]);
            ushort count = BitConverter.ToUInt16(retail, 9);
            Assert.Equal(count, BitConverter.ToUInt16(retail, 7));
            var record = retail.Length == 153 ? retail[27..] : new byte[126];
            using var w = new RO2.Network.Packets.PacketWriter(153);
            PrivateShopItemListPackets.WriteEntry(w, new PrivateShopItemListPackets.Entry(retail[0],
                BitConverter.ToUInt32(retail, 1), 0, 0, count, BitConverter.ToUInt64(retail, 11),
                BitConverter.ToUInt64(retail, 19), record));
            var built = w.ToArray();
            Assert.Equal(retail[..27], built[..27]);
            if (retail.Length == 153)
            {
                Assert.Equal(count, retail[27 + 14]);
                Assert.Equal(retail, built);
            }
        }
    }

    [Fact]
    public void BuyerEntry_RecordCarriesTheListedCountAndTheSellerCell()
    {
        
        
        
        var item = new InventoryItem { Id = 77, CharacterId = 10, ItemId = 11100004, Count = 5, Slot = 3, Durability = 0 };
        var entry = PrivateShopItemListPackets.BuyerEntry(new PrivateShopRegistry.Listing(2, 77, 2, 100), item);
        Assert.Equal(((byte)2, (byte)0, (byte)0, (ushort)2), (entry.Index, entry.Bag, entry.Slot, entry.Count));
        Assert.Equal(2, entry.Record126[14]);
        Assert.Equal(new byte[] { 1, 53 }, entry.Record126[15..17]);   
        Assert.Equal(5, item.Count);

        using var w = new RO2.Network.Packets.PacketWriter(153);
        PrivateShopItemListPackets.WriteEntry(w, entry);
        var built = w.ToArray();
        Assert.Equal(new byte[] { 0, 0 }, built[5..7]);                  
        Assert.Equal(2, BitConverter.ToUInt16(built, 7));                
        Assert.Equal(2, BitConverter.ToUInt16(built, 9));                
        Assert.Equal(2, built[27 + 14]);                                 
    }

    [Fact]
    public void ItemListUpdate2662_EmptyListIsTheBareHeader()
    {
        
        var built = PrivateShopItemListPackets.BuildItemListUpdate2662(BuyerHandle, Array.Empty<PrivateShopItemListPackets.Entry>());
        Assert.Equal(1 + 1 + 58, built.Length);
        Assert.Equal(new byte[] { 0x01, 58 }, built[..2]);
        Assert.All(built[2..46], b => Assert.Equal(0, b));               
        Assert.Equal(0, BitConverter.ToUInt16(built, 2 + 44));
        Assert.Equal(BuyerHandle, BitConverter.ToUInt32(built, 2 + 46));
    }

    [Fact]
    public void BuyAnswer2659_IsResultPaidWalletAndTheRequestEcho()
    {
        
        
        Assert.Equal(Convert.FromHexString("0116" + "0000" + "d807000000000000" + "8813000000000000" + "0200" + "0300"),
            PrivateShopItemSalePackets.BuildBuyAnswer2659(2008, 5000, new byte[] { 3, 0 }));
    }

    [Theory]
    [InlineData(PrivateShopItemSalePackets.ResultAlreadySold, "0102de59")]
    [InlineData(PrivateShopItemSalePackets.ResultNotEnoughMoney, "0102f259")]
    public void BuyRefusal2659_IsTheCodeAlone(ushort code, string hex) =>
        
        Assert.Equal(Convert.FromHexString(hex), PrivateShopItemSalePackets.BuildBuyRefusal2659(code));

    [Fact]
    public void BuyRequest1482_ParsesTheBuilderLayout()
    {
        
        var body = Convert.FromHexString("0108" + "11804f09" + "0200" + "0003");
        Assert.True(PrivateShopItemSalePackets.TryParseBuyRequest1482(body, out var owner, out var indices));
        Assert.Equal(OwnerHandle, owner);
        Assert.Equal(new byte[] { 0, 3 }, indices);

        
        Assert.True(PrivateShopItemSalePackets.TryParseBuyRequest1482(Convert.FromHexString("0108" + "11804f09" + "0100" + "05" + "aabbccdd"),
            out _, out var one));
        Assert.Equal(new byte[] { 5 }, one);

        
        Assert.False(PrivateShopItemSalePackets.TryParseBuyRequest1482(Convert.FromHexString("0107" + "11804f09" + "0200" + "00"), out _, out _));
    }

    [Fact]
    public void ItemSaled2655_CarriesBuyerWalletAndTheSellerEntryWithItsCell()
    {
        
        
        var item = new InventoryItem { Id = 0x0E043DA80E619B7EUL, CharacterId = 10, ItemId = 0x0153B000u, Count = 3, Slot = 20 + 2 * 35 + 12, Durability = 0 };
        var listing = new PrivateShopRegistry.Listing(4, item.Id, 2, 777);
        var entry = PrivateShopItemListPackets.SellerEntry(listing, item);
        Assert.Equal((byte)4, entry.Bag);
        Assert.Equal((byte)12, entry.Slot);

        var title = Encoding.Unicode.GetBytes("shop").Concat(new byte[PrivateShopRegistry.NameBytes - 8]).ToArray();
        var built = PrivateShopSalePackets.BuildItemSaled2655(title, BuyerHandle, 123_456_789L, new[] { entry });
        var blob = built.AsSpan(3).ToArray();
        Assert.Equal(new byte[] { 0x02, 211, 0 }, built[..3]);
        Assert.Equal(58 + 153, blob.Length);
        Assert.Equal(title, blob[2..44]);
        Assert.Equal(1, BitConverter.ToUInt16(blob, 44));
        Assert.Equal(BuyerHandle, BitConverter.ToUInt32(blob, 46));
        Assert.Equal(123_456_789UL, BitConverter.ToUInt64(blob, 50));
        Assert.Equal(4, blob[58]);                                        
        Assert.Equal(0x0153B000u, BitConverter.ToUInt32(blob, 58 + 1));   
        Assert.Equal(new byte[] { 4, 12 }, blob[(58 + 5)..(58 + 7)]);     
        Assert.Equal(2, BitConverter.ToUInt16(blob, 58 + 9));             
        Assert.Equal(item.Id, BitConverter.ToUInt64(blob, 58 + 11));      
        Assert.Equal(777UL, BitConverter.ToUInt64(blob, 58 + 19));        
        Assert.Equal(WorldEntryService.BuildItemRecord126(item), blob[(58 + 27)..]);
    }

    [Fact]
    public void SaledList2656_IsBuyerCountIds()
    {
        
        Assert.Equal(Convert.FromHexString("010e" + "11d94f09" + "0200" + "00b05301" + "39300000"),
            PrivateShopSalePackets.BuildSaledList2656(BuyerHandle, new uint[] { 0x0153B000u, 12345 }));
    }

    [Fact]
    public void Registry_WatchingFollowsTheLastOpenedShopAndTheClose()
    {
        var a = new PlayerState { SessionId = 101, CharacterId = 1010, EntityId = 0x09000011u };
        var b = new PlayerState { SessionId = 102, CharacterId = 1020, EntityId = 0x09000111u };
        var buyer = new PlayerState { SessionId = 103, CharacterId = 1030, EntityId = BuyerHandle };
        PrivateShopRegistry.SetName(a, Encoding.Unicode.GetBytes("a"));
        PrivateShopRegistry.SetName(b, Encoding.Unicode.GetBytes("b"));
        Assert.True(PrivateShopRegistry.IsOpen(a));

        PrivateShopRegistry.AddViewer(a, buyer);
        Assert.True(PrivateShopRegistry.IsWatching(a, buyer));
        PrivateShopRegistry.AddViewer(b, buyer);                     
        Assert.False(PrivateShopRegistry.IsWatching(a, buyer));
        Assert.True(PrivateShopRegistry.IsViewer(a, buyer));         
        Assert.Single(PrivateShopRegistry.ViewersSnapshot(a));
        Assert.Single(PrivateShopRegistry.ViewersSnapshot(a));       
        PrivateShopRegistry.StopWatching(buyer);                     
        Assert.False(PrivateShopRegistry.IsWatching(b, buyer));
        Assert.True(PrivateShopRegistry.IsViewer(b, buyer));

        PrivateShopRegistry.TakeViewers(a);                          
        Assert.False(PrivateShopRegistry.IsOpen(a));
    }

    [Fact]
    public async Task Purchase_PartialListingMergesThenSplitsAndPaysTheSeller()
    {
        const uint Potion = 11100004;
        var (owner, buyer, inventory) = Shop(ownerGold: 50, buyerGold: 1000);
        var sellerStack = Add(inventory, owner, Potion, count: 5, slot: 3);
        var held = Add(inventory, buyer, Potion, count: 9, slot: 0);
        Assert.True(PrivateShopRegistry.TryAdd(owner, sellerStack, 2, 100, out var listing));
        var items = new Dictionary<uint, ItemData> { [Potion] = new ItemData { ID = Potion, Stack_Max = 10 } };

        var sale = await PrivateShopBuyItemHandler.PurchaseAsync(buyer, owner, new[] { listing.Index }, inventory, items);

        Assert.Equal(0, sale.Refusal);
        Assert.Equal(100UL, sale.Total);
        Assert.Equal(900, buyer.Gold);
        Assert.Equal(900, sale.BuyerGoldAfter);
        Assert.Equal(150, owner.Gold);
        Assert.Equal(150, sale.SellerGoldAfter);
        
        Assert.Equal(2, sale.BuyerStacks.Count);
        Assert.Same(held, sale.BuyerStacks[0]);
        Assert.Equal(10, held.Count);
        var split = sale.BuyerStacks[1];
        Assert.Equal((buyer.CharacterId, Potion, 1, (short)1), (split.CharacterId, split.ItemId, split.Count, split.Slot));
        Assert.NotEqual(sellerStack.Id, split.Id);
        
        Assert.Equal((owner.CharacterId, 3), (sellerStack.CharacterId, sellerStack.Count));
        Assert.Equal(3, sale.SellerRecords.Single()[14]);
        Assert.Equal(new[] { Potion }, sale.SoldItemIds);
        
        Assert.True(PrivateShopRegistry.TryGet(owner, out _, out var left));
        Assert.Empty(left);
        var again = await PrivateShopBuyItemHandler.PurchaseAsync(buyer, owner, new[] { listing.Index }, inventory, items);
        Assert.Equal(PrivateShopItemSalePackets.ResultAlreadySold, again.Refusal);
    }

    [Fact]
    public async Task Purchase_WholeStackMovesWithItsUid()
    {
        const uint Sword = 13100001;
        var (owner, buyer, inventory) = Shop(ownerGold: 0, buyerGold: 2008);
        var sword = Add(inventory, owner, Sword, count: 1, slot: 7);
        sword.Refine = 5;
        Assert.True(PrivateShopRegistry.TryAdd(owner, sword, 1, 2008, out var listing));

        var sale = await PrivateShopBuyItemHandler.PurchaseAsync(buyer, owner, new[] { listing.Index }, inventory,
            new Dictionary<uint, ItemData> { [Sword] = new ItemData { ID = Sword, Stack_Max = 1 } });

        Assert.Equal(0, sale.Refusal);
        Assert.Same(sword, sale.BuyerStacks.Single());
        Assert.Equal((buyer.CharacterId, (short)0, (byte)5), (sword.CharacterId, sword.Slot, sword.Refine));
        var sellerRecord = sale.SellerRecords.Single();
        Assert.Equal(sword.Id, BitConverter.ToUInt64(sellerRecord, 0));
        Assert.Equal(0, sellerRecord[14]);                      
        Assert.Equal(new byte[] { 1, 57 }, sellerRecord[15..17]); 
        Assert.Equal(0, buyer.Gold);
        Assert.Equal(2008, owner.Gold);
    }

    [Fact]
    public async Task Purchase_RefusalsMoveNothing()
    {
        const uint Gem = 12000001;
        var items = new Dictionary<uint, ItemData> { [Gem] = new ItemData { ID = Gem, Stack_Max = 1 } };

        
        var (owner, buyer, inventory) = Shop(ownerGold: 0, buyerGold: 99);
        var gem = Add(inventory, owner, Gem, count: 1, slot: 0);
        Assert.True(PrivateShopRegistry.TryAdd(owner, gem, 1, 100, out var listing));
        var poor = await PrivateShopBuyItemHandler.PurchaseAsync(buyer, owner, new[] { listing.Index }, inventory, items);
        Assert.Equal(PrivateShopItemSalePackets.ResultNotEnoughMoney, poor.Refusal);

        
        buyer.Gold = 100;
        for (short s = 0; s < 20; s++) Add(inventory, buyer, Gem, count: 1, slot: s);
        var full = await PrivateShopBuyItemHandler.PurchaseAsync(buyer, owner, new[] { listing.Index }, inventory, items);
        Assert.Equal(PrivateShopItemSalePackets.ResultNoBagRoom, full.Refusal);

        
        var unknown = await PrivateShopBuyItemHandler.PurchaseAsync(buyer, owner, new[] { (byte)(listing.Index + 1) }, inventory, items);
        Assert.Equal(PrivateShopItemSalePackets.ResultAlreadySold, unknown.Refusal);

        
        var stranger = new PlayerState { SessionId = 9, CharacterId = 90, EntityId = 0x09AAAA11u, Gold = 1000 };
        Assert.Equal(PrivateShopItemSalePackets.ResultWrongBuyer,
            (await PrivateShopBuyItemHandler.PurchaseAsync(stranger, owner, new[] { listing.Index }, inventory, items)).Refusal);
        Assert.Equal(PrivateShopItemSalePackets.ResultWrongBuyer,
            (await PrivateShopBuyItemHandler.PurchaseAsync(owner, owner, new[] { listing.Index }, inventory, items)).Refusal);

        
        PrivateShopRegistry.TakeViewers(owner);
        Assert.Equal(PrivateShopItemSalePackets.ResultShopClosed,
            (await PrivateShopBuyItemHandler.PurchaseAsync(buyer, owner, new[] { listing.Index }, inventory, items)).Refusal);

        Assert.Equal((owner.CharacterId, (short)0), (gem.CharacterId, gem.Slot));
        Assert.Equal((100L, 0L), (buyer.Gold, owner.Gold));
        Assert.True(PrivateShopRegistry.TryGet(owner, out _, out var still));
        Assert.Single(still);
    }

 /// <summary>An open shop (1478) with the buyer looking at it (1481).</summary>
    private static (PlayerState Owner, PlayerState Buyer, FakeInventory Inventory) Shop(long ownerGold, long buyerGold)
    {
        var owner = new PlayerState { SessionId = 1, CharacterId = 10, EntityId = OwnerHandle, Gold = ownerGold };
        var buyer = new PlayerState { SessionId = 2, CharacterId = 20, EntityId = BuyerHandle, Gold = buyerGold };
        PrivateShopRegistry.SetName(owner, Encoding.Unicode.GetBytes("shop"));
        PrivateShopRegistry.AddViewer(owner, buyer);
        return (owner, buyer, new FakeInventory());
    }

    private static InventoryItem Add(FakeInventory inventory, PlayerState who, uint itemId, int count, short slot)
    {
        var item = new InventoryItem
        {
            Id = 0x0E00_0000_0000_0000UL + (ulong)inventory.Items.Count + 1,
            CharacterId = who.CharacterId, ItemId = itemId, Count = count, Slot = slot, StorageType = 0, Durability = 0,
        };
        inventory.Items.Add(item);
        return item;
    }

    private sealed class FakeInventory : IInventoryRepository
    {
        public readonly List<InventoryItem> Items = new();
        public Task<IReadOnlyList<InventoryItem>> GetByCharacterAsync(uint characterId) =>
            Task.FromResult<IReadOnlyList<InventoryItem>>(Items.Where(i => i.CharacterId == characterId).ToList());
        public Task<IReadOnlyList<InventoryItem>> GetEquipmentAsync(uint characterId) =>
            Task.FromResult<IReadOnlyList<InventoryItem>>(Items.Where(i => i.CharacterId == characterId && i.StorageType == 1).ToList());
        public Task<InventoryItem?> GetByIdAsync(ulong id) => Task.FromResult(Items.FirstOrDefault(i => i.Id == id));
        public async Task AddItemAsync(InventoryItem item)
        {
            if (item.Id == 0) item.Id = await GetNextIdAsync();
            Items.Add(item);
        }
        public Task RemoveItemAsync(ulong itemId) { Items.RemoveAll(i => i.Id == itemId); return Task.CompletedTask; }
        public Task UpdateItemAsync(InventoryItem item) => Task.CompletedTask;
        public Task<ulong> GetNextIdAsync() => Task.FromResult(Items.Count == 0 ? 1UL : Items.Max(i => i.Id) + 1);
        public Task<int> RepairSlotsAsync(uint characterId) => Task.FromResult(0);
    }
}
