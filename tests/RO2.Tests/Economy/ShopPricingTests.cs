using RO2.Economy.Domain;
using Xunit;

namespace RO2.Tests.Economy;

public class ShopPricingTests
{
    [Theory]
    [InlineData(250, 3, 750)]
    [InlineData(100000, 1, 100000)]
    [InlineData(0, 5, 0)]
    public void BuyCost_IsUnitTimesQuantity(long unit, long qty, long expected)
        => Assert.Equal(expected, ShopPricing.BuyCost(unit, qty));

    [Theory]
    [InlineData(10, 3, 3)]   // asked for more than held -> clamped to held
    [InlineData(2, 5, 2)]    // asked for less than held -> as asked
    [InlineData(5, 5, 5)]
    public void SellQty_NeverExceedsHeld(int requested, int held, int expected)
        => Assert.Equal(expected, ShopPricing.SellQty(requested, held));

    [Theory]
    [InlineData(1, 5, 5)]              // 21330011 sells for 1 each
    [InlineData(100, 3, 300)]          // 30000311 sells for 100 each
    [InlineData(1_000_000, 255, 255_000_000)] // large stack stays in 64-bit
    public void SellProceeds_IsUnitTimesQuantitySold(long unit, int qty, long expected)
        => Assert.Equal(expected, ShopPricing.SellProceeds(unit, qty));

    [Fact]
    public void ShopCodes_HaveTheirRetailValues()
    {
        Assert.Equal((ushort)1, ShopCodes.BuyFailGeneric);
        Assert.Equal((ushort)20084, ShopCodes.BuyFailMastery);
        Assert.Equal((ushort)20020, ShopCodes.BuyFailBagFull);
        Assert.Equal((ushort)20166, ShopCodes.BuyFailPossession);
        Assert.Equal((ushort)0, ShopCodes.SellResultSold);
        Assert.Equal((ushort)1, ShopCodes.SellResultNothingSold);
        Assert.Equal((short)1, ShopCodes.CancelRefused);
        Assert.Equal((short)20020, ShopCodes.CancelBagFull);
    }
}
