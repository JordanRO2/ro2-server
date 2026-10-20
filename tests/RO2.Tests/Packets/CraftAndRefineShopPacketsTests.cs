using System;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Byte-exact checks for craft (2536/2539/2540/2541/2398), refine-shop (2999/3006) and private shop (2398) frames.
/// Each expected body is a known-good frame from a reference session.
/// </summary>
public class CraftAndRefineShopPacketsTests
{
    private const uint Crafter = 0x0AA9D811u;

    [Fact]
    public void AckMakeCraftItem2539_MatchesTheCraftStartAck() =>
        Assert.Equal(Convert.FromHexString("010a0a0016e411d8a90a0000"),
            ShopItemPackets.BuildAckMakeCraftItem2539(Crafter));

    [Theory]
    [InlineData(ShopItemPackets.CraftPhaseBegin, (ushort)196, "0111110018e40000000000000111d8a90ac400")]
    [InlineData(ShopItemPackets.CraftPhaseComplete, (ushort)300, "0111110018e40000000000000211d8a90a2c01")]
    [InlineData(ShopItemPackets.CraftPhaseTerminate, (ushort)0, "0111110018e40000000000000311d8a90a0000")]
    public void NotifyMakeCraftItem2540_ReproducesEachPhase(byte phase, ushort recipe, string retailHex) =>
        Assert.Equal(Convert.FromHexString(retailHex),
            ShopItemPackets.BuildNotifyMakeCraftItem2540(Crafter, phase, recipe));

    [Fact]
    public void AckMakeCraftItemInfo2541_ReproducesRetailFrame()
    {
        
        var retail = Convert.FromHexString(
            "028a008a0019e411d8a90a00006435c83c8c12a83d040e1cad53010000010419000000000000000000000000000000" +
            "00000000000000000000000000000000000000000000000000000000000000000000000000ae390000000000000000" +
            "0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000");
        var record = retail.AsSpan(15).ToArray();
        Assert.Equal(126, record.Length);
        Assert.Equal(retail, ShopItemPackets.BuildAckMakeCraftItemInfo2541(Crafter, 0x3564, record));
    }

    [Fact]
    public void CoolTimeWaitCraftIndex2536_ReproducesRetailFrame() =>
        Assert.Equal(Convert.FromHexString("0c0000000100c4000100000000000000"),
            ShopItemPackets.BuildCoolTimeWaitCraftIndex2536(196, 1));

    [Theory]
    [InlineData(RO2.Server.Zone.Handlers.Rmi.CraftItemHandler.CraftingState,
        "011b1b00eae211d8a90a11d8a90a130000000000000200000000000000")]
    [InlineData((byte)0, "011b1b00eae211d8a90a11d8a90a000000000000000200000000000000")]
    public void CraftChangeState2398_ReproducesStartAndEnd(byte state, string retailHex) =>
        Assert.Equal(Convert.FromHexString(retailHex),
            CombatPackets.BuildChangeState2398(Crafter, state, 0,
                RO2.Server.Zone.Handlers.Rmi.CraftItemHandler.ChangeStateTrailer));

    [Fact]
    public void BeginRefineShop2999_EchoesTheRequestValue() =>
        
        Assert.Equal(Convert.FromHexString("011010008ee41156510a0100000000000000"),
            ShopItemPackets.BuildBeginRefineShop2999(0x0A515611u, 1));

    [Fact]
    public void RandomRefineCommission3006_CarriesTheOwnerCommission() =>
        Assert.Equal(Convert.FromHexString("011010009de411528c0a0100000000000000"),
            ShopItemPackets.BuildRandomRefineCommission3006(0x0A8C5211u, 1));

    
    
    
    
    [Fact]
    public void BeginRefineShop2999_ReproducesTheZeroRequestFrame() =>
        Assert.Equal(Convert.FromHexString("011010008ee4115ffe070000000000000000"),
            ShopItemPackets.BuildBeginRefineShop2999(0x07FE5F11u, 0));

    [Fact]
    public void RandomRefineCommission3006_ReproducesTheNoCommissionFrame() =>
        Assert.Equal(Convert.FromHexString("011010009de411168e070000000000000000"),
            ShopItemPackets.BuildRandomRefineCommission3006(0x078E1611u, 0));

    [Fact]
    public void PrivateShopBegin2398_ReproducesTheShopStateFrame() =>
        
        
        Assert.Equal(Convert.FromHexString("011b1b00eae21128ca0a1128ca0a1f0000000000000200000007000000"),
            CombatPackets.BuildChangeState2398(0x0ACA2811u, PrivateShopRegistry.ShopOpenState,
                targetHandle: PrivateShopRegistry.ShopChangeStateWord23,
                trailing: PrivateShopRegistry.ShopChangeStateWord19));
}
