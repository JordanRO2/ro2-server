using RO2.Domain.Entities;
using RO2.Network.Packets;
using RO2.Server.Chat;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// 1662 ReqBestrowAttribute and 2983 AnsBestrowAttribute (<see cref="BestrowAttributeHandler"/>), the element field
/// of the worn-item record (<see cref="EquipViewNotifyPackets.WriteEquipRecord28"/>), and the messenger block list's
/// unknown-name answer (result codes 23006 and 23510, <see cref="BlockListPackets"/>), checked against retail frames.
/// </summary>
public class UnitA9BestowElementTests
{
    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    [Fact]
    public void Refusals_AreRetailsElevenBytes()
    {
        
        
        
        Assert.Equal("215f000000000000000000", Hex(BestrowAttributeHandler.BuildRefusal2983(BestrowAttributeHandler.ResultSameElement)));
        Assert.Equal("de59000000000000000000", Hex(BestrowAttributeHandler.BuildRefusal2983(BestrowAttributeHandler.ResultNoStone)));
        Assert.Equal("355f000000000000000000", Hex(BestrowAttributeHandler.BuildRefusal2983(BestrowAttributeHandler.ResultAlreadyHasElement)));
    }

    [Theory]
    
    [InlineData("424cabdba73d040ec912280ca83d040e", 5, "0000c912280ca83d040e05")]
    
    [InlineData("c38e83dba73d040ec912280ca83d040e", 0, "0000c912280ca83d040e00")]
    public void Success_EchoesTheTargetAndTheStoneElement(string requestHex, byte element, string answerHex)
    {
        var req = Convert.FromHexString(requestHex);
        var target = BitConverter.ToUInt64(req, 8);
        Assert.Equal(answerHex, Hex(BestrowAttributeHandler.BuildSuccess2983(target, element)));
    }

    [Fact]
    public void RefusalRule_MatchesRetailsThreeCases()
    {
        
        Assert.Equal(BestrowAttributeHandler.ResultSameElement, BestrowAttributeHandler.RefusalFor(0, 0));
        
        Assert.Equal(BestrowAttributeHandler.ResultAlreadyHasElement, BestrowAttributeHandler.RefusalFor(5, 5));
        Assert.Equal(BestrowAttributeHandler.ResultAlreadyHasElement, BestrowAttributeHandler.RefusalFor(1, 4));
        
        Assert.Null(BestrowAttributeHandler.RefusalFor(3, 0));
        Assert.Null(BestrowAttributeHandler.RefusalFor(0, 4));
    }

    [Fact]
    public void UseItem_BeforeTheSuccess_IsRetailsFrame()
    {
        
        
        var use = CombatPackets.BuildUseItem2329(0x070F5211, 10, 3, 0x0E043DA8072C1AC2UL, 203, useSource: 1);
        Assert.Equal("01161600a6e211520f0700000a03c21a2c07a83d040ecb01", Hex(use));
        Assert.Equal("0000cc615e06a83d040e05", Hex(BestrowAttributeHandler.BuildSuccess2983(0x0E043DA8065E61CCUL, 5)));
    }

    [Fact]
    public void WornRecord_CarriesTheElementAtPlusNine()
    {
        
        
        
        const string retail = "0659dbfe00000000000514000000000000000058f3ad000000000000";
        var item = new InventoryItem { ItemId = 16702297, Slot = 6, StorageType = 1, Refine = 20, Element = 5 };
        using var w = new PacketWriter(EquipViewNotifyPackets.RecordSize);
        EquipViewNotifyPackets.WriteEquipRecord28(w, item);
        var ours = Hex(w.ToArray());
        Assert.Equal(retail[..38], ours[..38]);
        Assert.Equal("00000000", ours[38..46]);
        Assert.Equal(retail[46..], ours[46..]);
        Assert.Equal("05", ours[18..20]);

        
        item.Element = 0;
        using var w0 = new PacketWriter(EquipViewNotifyPackets.RecordSize);
        EquipViewNotifyPackets.WriteEquipRecord28(w0, item);
        Assert.Equal("00", Hex(w0.ToArray())[18..20]);
    }

    [Fact]
    public void UnknownBlockName_Is18WithTheEmptyRecord()
    {
        
        
        Assert.Equal("VVAND", BlockListPackets.ReadAddBlockName(Convert.FromHexString("0c000000055600560041004e00440000")));
        Assert.Equal("12000a00000000000000000000000000",
            Hex(BlockListPackets.BuildAddBlockRefusal(BlockListPackets.AddBlockResultNotFound)));
        
        Assert.Equal("07000a00000000000000000000000000",
            Hex(BlockListPackets.BuildAddBlockRefusal(BlockListPackets.AddBlockResultAlreadyRegistered)));
    }
}
