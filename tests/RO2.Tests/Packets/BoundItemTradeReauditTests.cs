using System.Buffers.Binary;
using System.Text.Json;
using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Services;
using Xunit;
using RO2.Inventory.Domain;

namespace RO2.Tests.Packets;

/// <summary>
/// 1353 ReqPCTradeMove: a bound instance is refused with 2384 result 20063 (TradeMoveHandler.RefusedAsNotTradeable).
/// Each frame below is a reference pair (enc-based framing, body after the RMI header): the offerer's 1353
/// and the 2384 it received.
/// </summary>
public class BoundItemTradeReauditTests
{
 /// <summary>Builds the refusal exactly as TradeMoveHandler does, from the 1353 body alone.</summary>
    private static byte[] RefusalFor(byte[] request)
    {
        
        uint handle = BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(6));
        ulong uid = BinaryPrimitives.ReadUInt64LittleEndian(request.AsSpan(10));
        uint template = BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(18));
        int count = BinaryPrimitives.ReadUInt16LittleEndian(request.AsSpan(22));
        return TradePackets.BuildOnDragTradePC(handle,
            TradePackets.BuildRefusedDragRecord(uid, template, count), TradePackets.DragItemNotTradeable);
    }

    [Theory]
    
    
    [InlineData("01161600b6e21185cd09a7079509a83d040e96dbfe000100",
        "028c008c00b7e2000000005f4e1185cd09a7079509a83d040e96dbfe0000000100")]
    
    
    
    [InlineData("01161600b6e211ce570d1726210ea83d040eb06f54010200",
        "028c008c00b7e2000000005f4e11ce570d1726210ea83d040eb06f540100000200")]
    public void BoundItemRefusalReproducesRetailFrame(string requestHex, string answerPrefixHex)
    {
        
        
        var expected = Convert.FromHexString(answerPrefixHex + new string('0', 2 * 110));
        Assert.Equal(143, expected.Length);
        Assert.Equal(expected, RefusalFor(Convert.FromHexString(requestHex)));
    }

    private static ItemData Template(uint id)
    {
        
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "data", "ItemInfo.json")))
            root = root.Parent;
        if (root == null)
            throw new DirectoryNotFoundException("no ancestor of " + AppContext.BaseDirectory + " holds data/ItemInfo.json");
        using var stream = File.OpenRead(Path.Combine(root.FullName, "data", "ItemInfo.json"));
        using var doc = JsonDocument.Parse(stream);
        foreach (var row in doc.RootElement.EnumerateArray())
        {
            if (row.GetProperty("ID").GetUInt32() != id) continue;
            return new ItemData
            {
                ID = id,
                Bind_Type = row.GetProperty("Bind_Type").GetByte(),
                Is_Trade = row.GetProperty("Is_Trade").GetByte(),
            };
        }
        throw new InvalidOperationException("ItemInfo has no row " + id);
    }

    [Fact]
    public void SameBindType1TemplateIsRefusedOnlyOnceWorn()
    {
        
        
        
        var template = Template(22310832u);
        Assert.Equal(1, template.Bind_Type);
        Assert.Equal(1, template.Is_Trade);

        var worn = new InventoryItem { Id = 0x0E043DA80E212617ul, ItemId = 22310832u, StorageType = 0, Bound = true };
        var unworn = new InventoryItem { Id = 0x0E043DA80D4EC9A9ul, ItemId = 22310832u, StorageType = 0, Bound = false };
        Assert.True(TradeMoveHandler.RefusedAsNotTradeable(template, worn));
        Assert.False(TradeMoveHandler.RefusedAsNotTradeable(template, unworn));
    }

    [Fact]
    public void BindType2TemplateIsAlwaysRefused()
    {
        
        
        var template = Template(16702358u);
        Assert.Equal(2, template.Bind_Type);
        Assert.Equal(1, template.Is_Trade);
        Assert.True(TradeMoveHandler.RefusedAsNotTradeable(template,
            new InventoryItem { ItemId = 16702358u, StorageType = 0, Bound = false }));
    }

    [Fact]
    public void UntradeableTemplateIsRefusedAndAnUnboundTradeableOneIsNot()
    {
        
        
        var untradeable = Template(11100121u);
        Assert.Equal(0, untradeable.Is_Trade);
        Assert.Equal(0, untradeable.Bind_Type);
        Assert.True(TradeMoveHandler.RefusedAsNotTradeable(untradeable,
            new InventoryItem { ItemId = 11100121u, StorageType = 0 }));

        var tradeable = Template(16701741u);
        Assert.Equal(1, tradeable.Bind_Type);
        Assert.False(TradeMoveHandler.RefusedAsNotTradeable(tradeable,
            new InventoryItem { ItemId = 16701741u, StorageType = 0, Bound = false }));
    }
}
