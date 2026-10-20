using RO2.Server.Zone.Services;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System;
using Xunit;
using static RO2.Tests.Packets.ReauditShared;

namespace RO2.Tests.Packets;

/// <summary>
/// Private-shop item sale (1482, answered by 2659 and 2662), with byte-exact expected bodies for two sales.
/// </summary>
public class PrivateShopItemSaleReauditTests
{
    
 /// <summary>Request 1482 buying indices 2, 1 and 0 (three entries) from the shop owner.</summary>
    private const string Retail1482A = "010911a8dd0a0300020100";

 /// <summary>Request 1482 buying index 5 from the shop owner.</summary>
    private const string Retail1482B = "010711942002010005";

    [Fact]
    public void BuyRequest1482_ParsesBothRetailRequests()
    {
        Assert.True(PrivateShopItemSalePackets.TryParseBuyRequest1482(Convert.FromHexString(Retail1482A), out var ownerA, out var idxA));
        Assert.Equal(0x0ADDA811u, ownerA);
        Assert.Equal(new byte[] { 2, 1, 0 }, idxA);

        Assert.True(PrivateShopItemSalePackets.TryParseBuyRequest1482(Convert.FromHexString(Retail1482B), out var ownerB, out var idxB));
        Assert.Equal(0x02209411u, ownerB);
        Assert.Equal(new byte[] { 5 }, idxB);
    }

    [Fact]
    public void BuyAnswer2659_IsRetailsAnswerToTheFirstSale()
    {
        
        
        Assert.True(PrivateShopItemSalePackets.TryParseBuyRequest1482(Convert.FromHexString(Retail1482A), out _, out var idx));
        Assert.Equal(Convert.FromHexString("01170000234ed2080000000034d26e4a020000000300020100"),
            PrivateShopItemSalePackets.BuildBuyAnswer2659(148000291, 9838711348, idx));
    }

    [Fact]
    public void BuyAnswer2659_IsRetailsAnswerToTheSecondSale()
    {
        
        Assert.True(PrivateShopItemSalePackets.TryParseBuyRequest1482(Convert.FromHexString(Retail1482B), out _, out var idx));
        Assert.Equal(Convert.FromHexString("0115000001ca9a3b00000000304274d301000000010005"),
            PrivateShopItemSalePackets.BuildBuyAnswer2659(1000000001, 7842578992, idx));
    }

    [Fact]
    public void ItemListUpdate2662_IsRetailsEmptiedShop()
    {
        
        
        var retail = Convert.FromHexString("013a" + new string('0', 2 * 46) + "1179e80a" + new string('0', 2 * 8));
        Assert.Equal(retail, PrivateShopItemListPackets.BuildItemListUpdate2662(0x0AE87911u, []));
    }

 /// <summary>Response 2662 after index 5 is bought: a 670-byte blob (58 + 4 x 153) listing the four remaining items, indices 1 to 4, count 99, price 10000001.</summary>
    private static readonly byte[] Retail2662B = Convert.FromHexString(
        "029e020000000000000000000000000000000000000000000000000000000000000000000000000000000000000000040011f8200200000000000000" +
        "0001c08a9303000063006300ca331c11a83d040e8196980000000000ca331c11a83d040ec08a9303000063021b000000000000000000000000000000" +
        "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000000002c08a930300006300630060f03e11a83d040e81969800000000" +
        "0060f03e11a83d040ec08a9303000063021e000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
        "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000003c08a930300006300630010f40a11a83d040e819698000000000010f40a11a83d040ec08a9303000063021d000000000000000000" +
        "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000000000000000000004be8a9303000063006300f1612611a83d040e81" +
        "96980000000000f1612611a83d040ebe8a93030000630216000000000000000000000000000000000000000000000000000000000000000000000000" +
        "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
        "00000000000000000000000000");

    [Fact]
    public void ItemListUpdate2662_IsRetailsFourItemList()
    {
        
        
        
        var blob = Retail2662B.AsSpan(3);
        Assert.Equal(670, blob.Length);
        var entries = new List<PrivateShopItemListPackets.Entry>();
        for (int k = 0; k < 4; k++)
        {
            var e = blob.Slice(58 + 153 * k, 153);
            entries.Add(new PrivateShopItemListPackets.Entry(e[0], BitConverter.ToUInt32(e[1..]), 0, 0,
                BitConverter.ToUInt16(e[9..]), BitConverter.ToUInt64(e[11..]), BitConverter.ToUInt64(e[19..]),
                e.Slice(27, 126).ToArray()));
        }
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, entries.Select(e => e.Index).ToArray());
        Assert.All(entries, e => Assert.Equal(10000001UL, e.Price));
        Assert.Equal(Retail2662B, PrivateShopItemListPackets.BuildItemListUpdate2662(0x0220F811u, entries));
    }
}
