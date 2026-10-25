using System.Buffers.Binary;
using RO2.Domain.Entities;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;
using RO2.Inventory.Domain;

namespace RO2.Tests.Packets;

/// <summary>
/// Tests for 2648 AnsDeletePrivateShopItem, the seller's item list sent after request 1475 ReqDeletePrivateShopItem
/// (PrivateShopDeleteItemHandler, <see cref="PrivateShopItemPackets"/>).
/// </summary>
/// <remarks>
/// The expected body is a compact marker, a length of 58 + 2 x 153 bytes for two items, result 0, an empty title, and
/// the two item entries. Fields the reference frame does not pin down follow the client's reader.
/// </remarks>
public class PrivateShopDeleteItem2648Tests
{
 /// <summary>Expected 2648 body: compact marker 2, length 0x016C (58 + 2 x 153), result 0, empty title, two items.</summary>
    internal const string Retail2648TwoItems =
        "026c01000000000000000000000000000000000000000000000000000000000000000000000000000000000000000002" +
        "0000000000000000000000000000f4cd06020134ff00ff0061d45a0fa83d040e01f90295000000000000000000000000" +
        "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
        "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000001f4cd06020136ff00ff0039a23911a83d040e01f90295000000" +
        "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
        "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
        "00000000000000000000000000000000000000000000000000000000000000";

    [Fact]
    public void Builder_reproduces_retails_2648()
    {
        var retail = Convert.FromHexString(Retail2648TwoItems);
        Assert.Equal(367, retail.Length);

        
        var body = PrivateShopItemPackets.BuildAnsDeletePrivateShopItem2648(new byte[PrivateShopRegistry.NameBytes], new[]
        {
            new PrivateShopItemPackets.SellerRow(Index: 0, ItemId: 0x0206CDF4, Bag: 1, Slot: 0x34, Count: 255,
                Uid: 0x0E043DA80F5AD461UL, Price: 2500000001),
            new PrivateShopItemPackets.SellerRow(Index: 1, ItemId: 0x0206CDF4, Bag: 1, Slot: 0x36, Count: 255,
                Uid: 0x0E043DA81139A239UL, Price: 2500000001),
        });

        Assert.Equal(retail, body);
    }

    [Fact]
    public void Empty_list_is_the_bare_58_byte_blob()
    {
        var title = new byte[PrivateShopRegistry.NameBytes];
        title[0] = (byte)'a';

        var body = PrivateShopItemPackets.BuildAnsDeletePrivateShopItem2648(title, Array.Empty<PrivateShopItemPackets.SellerRow>());

        Assert.Equal(2 + 58, body.Length);
        Assert.Equal(new byte[] { 0x01, 58 }, body[..2]);                 
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(2)));        
        Assert.Equal(title, body.AsSpan(4, PrivateShopRegistry.NameBytes).ToArray());
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(2 + 44)));   
        Assert.All(body[(2 + 46)..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void Rows_take_the_bag_cell_from_the_item_and_the_count_and_price_from_the_listing()
    {
        
        var item = new InventoryItem { Id = 0x0E043DA80F5AD461UL, ItemId = 0x0206CDF4, Count = 255, Slot = 2, StorageType = 0, Durability = 0 };
        var listing = new PrivateShopRegistry.Listing(Index: 0, Uid: item.Id, Count: 255, Price: 2500000001);

        var row = Assert.Single(PrivateShopItemPackets.SellerRows(new[] { (listing, item) }));

        Assert.Equal(new PrivateShopItemPackets.SellerRow(0, 0x0206CDF4, 1, 0x34, 255, 0x0E043DA80F5AD461UL, 2500000001), row);
    }

    [Fact]
    public void After_a_removal_the_list_is_the_remaining_live_listings()
    {
        
        var seller = new PlayerState { EntityId = 0x00100011u };
        var a = new InventoryItem { Id = 1001, ItemId = 0x0153B000, Count = 1, Slot = 0, StorageType = 0, Durability = 0 };
        var b = new InventoryItem { Id = 1002, ItemId = 21330037, Count = 9, Slot = 21, StorageType = 0, Durability = 0 };
        PrivateShopRegistry.BeginSetup(seller);
        Assert.True(PrivateShopRegistry.TryAdd(seller, a, 1, 2008, out var la));
        Assert.True(PrivateShopRegistry.TryAdd(seller, b, 4, 10, out var lb));

        Assert.True(PrivateShopRegistry.TryRemove(seller, la.Index));
        Assert.True(PrivateShopRegistry.TryGet(seller, out var title, out var listings));
        var rows = PrivateShopItemPackets.SellerRows(PrivateShopRegistry.Live(listings, new[] { a, b }));
        var body = PrivateShopItemPackets.BuildAnsDeletePrivateShopItem2648(title, rows);

        var row = Assert.Single(rows);
        Assert.Equal(lb.Index, row.Index);
        Assert.Equal((byte)2, row.Bag);                 
        Assert.Equal((byte)1, row.Slot);
        Assert.Equal(3 + 58 + 153, body.Length);
        var entry = body.AsSpan(3 + 58, 153);
        Assert.Equal(4, BinaryPrimitives.ReadUInt16LittleEndian(entry[7..]));  
        Assert.Equal(4, BinaryPrimitives.ReadUInt16LittleEndian(entry[9..]));
        Assert.All(entry[27..].ToArray(), x => Assert.Equal(0, x));            
    }
}
