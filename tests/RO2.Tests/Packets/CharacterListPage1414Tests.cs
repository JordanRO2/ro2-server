using RO2.Server.Zone.Handlers.Rmi;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Tests the 1414 Packet_CLMSR_SLOT_LIST page selector against two reference request bodies: selector 99
/// (the common case) and selector 1.
/// </summary>
public class CharacterListPage1414Tests
{
    private static byte[] Hex(string s) => Convert.FromHexString(s);

    [Fact]
    public void RetailBodies_CarryTheSelectorAtBlobPlus8()
    {
        Assert.Equal((ushort)99, ZoneClientInitHandler.ReadSelector(Hex("010a0a00c3da000000006300")));
        Assert.Equal((ushort)1, ZoneClientInitHandler.ReadSelector(Hex("010a0a00c3da000000000100")));
        
        Assert.Equal((ushort)99, ZoneClientInitHandler.ReadSelector(Hex("010a0a00c3da0000")));
    }

    [Fact]
    public void Selector1_IsTheFirstFiveOfTheList()
    {
        
        
        var nine = Enumerable.Range(1, 9).ToList();
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, ZoneClientInitHandler.SelectPage(nine, 1).ToArray());
        Assert.Equal(nine.ToArray(), ZoneClientInitHandler.SelectPage(nine, 99).ToArray());
        Assert.Equal(new[] { 6, 7, 8, 9 }, ZoneClientInitHandler.SelectPage(nine, 2).ToArray());
    }

    [Fact]
    public void Selector1_OnAShortAccount_IsTheWholeList()
    {
        
        var two = new List<int> { 10, 20 };
        Assert.Equal(two.ToArray(), ZoneClientInitHandler.SelectPage(two, 1).ToArray());
    }

    [Fact]
    public void PagedAnswer_2820_IsRetailsBody()
    {
        
        Assert.Equal(Hex("060000008f6508000000"), ZoneClientInitHandler.BuildPreOccupyNameAck2820(550287));
    }

    [Fact]
    public void UnobservedSelectors_FallBackToTheWholeList()
    {
        var nine = Enumerable.Range(1, 9).ToList();
        Assert.Equal(nine.ToArray(), ZoneClientInitHandler.SelectPage(nine, 0).ToArray());
        Assert.Equal(nine.ToArray(), ZoneClientInitHandler.SelectPage(nine, 3).ToArray()); 
    }
}
