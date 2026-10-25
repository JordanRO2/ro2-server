using RO2.Domain.Entities;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Models;
using RO2.Server.Zone.Services;
using Xunit;
using RO2.Inventory.Domain;

namespace RO2.Tests.Packets;

/// <summary>
/// Tests the NPC shop buy-back: RMI 2377 OnCancelSellItem as built by <see cref="CancelSellItemHandler"/>,
/// and the <see cref="BuyBackLedger"/> it draws from.
/// </summary>
/// <remarks>
/// Both vectors are reference bodies. The success buys back 248 units of item 21330037 (Price_Sell 1); a
/// repeat request for a uid bought back moments earlier is refused.
/// </remarks>
public class CancelSellItem2377Tests
{
    private const string RetailSuccess =
        "0298009800c5e2118a130a00009ba1081300000000c4d3e90da83d040e757845010000f8041800000000000000"
        + "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000"
        + "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000"
        + "000000000000000000000000c4d3e90da83d040e";

    private const string RetailRefusal =
        "0298009800c5e211a5210b01009a0bf10f00000000000000000000000000000000000000000000000000000000"
        + "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000"
        + "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000"
        + "0000000000000000000000007234090ea83d040e";

    [Fact]
    public void Success_ReproducesRetailFrame()
    {
        var retail = Convert.FromHexString(RetailSuccess);
        var record = retail.AsSpan(3 + 18, 126).ToArray();

        var body = CancelSellItemHandler.Build2377(0x0A138A11u, 0, 319_332_763, record, 0x0E043DA80DE9D3C4UL);

        Assert.Equal(155, body.Length);
        Assert.Equal(retail, body);
    }

    [Fact]
    public void Refusal_ReproducesRetailFrame()
    {
        var retail = Convert.FromHexString(RetailRefusal);

        var body = CancelSellItemHandler.Build2377(0x0B21A511u, CancelSellItemHandler.RefusedResult,
            267_455_386, null, 0x0E043DA80E093472UL);

        Assert.Equal(retail, body);
    }

    [Fact]
    public void Ledger_GivesEachSaleBackOnce_AtWhatItPaid()
    {
        var player = new PlayerState { EntityId = 0x00100011u, Gold = 1000 };
        var stack = new InventoryItem { Id = 42, CharacterId = 7, ItemId = 21330037, Count = 250, Durability = 0 };

        BuyBackLedger.RecordSale(player, stack, soldQty: 248, paid: 248);
        stack.Count = 2; 

        Assert.Equal(new ulong[] { 42 }, BuyBackLedger.UidsOf(player));
        Assert.True(BuyBackLedger.TryTake(player, 42, out var entry, out var index));
        Assert.Equal(248, entry!.Item.Count);
        Assert.Equal(248, entry.Paid);
        Assert.Equal(0, index);

        
        Assert.False(BuyBackLedger.TryTake(player, 42, out _, out _));
        Assert.Empty(BuyBackLedger.UidsOf(player));

        BuyBackLedger.Restore(player, entry, index);
        Assert.Equal(new ulong[] { 42 }, BuyBackLedger.UidsOf(player));
    }

    [Fact]
    public void Ledger_KeepsTheTenNewest_AndReappendsAResale()
    {
        var player = new PlayerState { EntityId = 0x00100011u };
        for (ulong uid = 1; uid <= 11; uid++)
            BuyBackLedger.RecordSale(player, new InventoryItem { Id = uid, ItemId = 1 }, 1, 1);

        Assert.Equal(new ulong[] { 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 }, BuyBackLedger.UidsOf(player));

        BuyBackLedger.RecordSale(player, new InventoryItem { Id = 5, ItemId = 1 }, 1, 1);
        Assert.Equal(new ulong[] { 2, 3, 4, 6, 7, 8, 9, 10, 11, 5 }, BuyBackLedger.UidsOf(player));
    }
}
