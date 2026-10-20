using RO2.Domain.GameData.Tables;
using RO2.Server.Zone.Handlers.Rmi;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Grinding-reset price of 1681 ReqInitGrindingOpt (<see cref="InitGrindingOptHandler.ResetPrice"/>), checked against the price the client quotes in the request. Expected values use the target's ability value from the 3013 answer and the ItemInfo and Grinding_Info data tables.
/// </summary>
public class InitGrindingOpt1681Tests
{
    private static readonly GrindingInfo[] ShippedGrindingInfo =
    {
        new() { INDEX = 1, Range_Rate = -1000, Star_Val = 1, Need_Gold = 10000, Reset_Gold = 50 },
        new() { INDEX = 2, Range_Rate = 0, Star_Val = 2, Need_Gold = 10000, Reset_Gold = 100 },
        new() { INDEX = 3, Range_Rate = 1000, Star_Val = 3, Need_Gold = 20000, Reset_Gold = 150 },
        new() { INDEX = 4, Range_Rate = 2000, Star_Val = 4, Need_Gold = 30000, Reset_Gold = 200 },
        new() { INDEX = 5, Range_Rate = 3000, Star_Val = 5, Need_Gold = 40000, Reset_Gold = 250 },
    };

    [Theory]
    
    [InlineData(30300, 629, 36559u, 157250L)]
    
    [InlineData(98300, 663, 100634u, 99450L)]
    
    [InlineData(16900, 561, 19231u, 112200L)]
    
    [InlineData(30200, 629, 35468u, 125800L)]
    
    [InlineData(14900, 561, 18010u, 140250L)]
    
    [InlineData(118400, 663, 132574u, 132600L)]
    public void ReproducesRetailQuotes(int abiliVal, int itemValueLv, uint abilityValue, long expected)
    {
        Assert.Equal(expected, InitGrindingOptHandler.ResetPrice(abiliVal, itemValueLv, abilityValue, ShippedGrindingInfo, discRate: 0));
    }

    [Fact]
    public void AnUngroundItem_IsPricedAtTheFirstRow()
    {
        
        
        Assert.Equal(50L * 629, InitGrindingOptHandler.ResetPrice(30300, 629, 0u, ShippedGrindingInfo, discRate: 0));
    }

    [Fact]
    public void AnAbilityAboveEveryRow_TakesTheLastRow()
    {
        
        Assert.Equal(250L * 629, InitGrindingOptHandler.ResetPrice(30300, 629, 60600u, ShippedGrindingInfo, discRate: 0));
    }

    [Fact]
    public void TheVoucherDiscountApplies()
    {
        
        Assert.Equal(0L, InitGrindingOptHandler.ResetPrice(30300, 629, 36559u, ShippedGrindingInfo, discRate: 10000));
        Assert.Equal(78625L, InitGrindingOptHandler.ResetPrice(30300, 629, 36559u, ShippedGrindingInfo, discRate: 5000));
    }

    [Fact]
    public void ATargetWithoutAbiliVal_CannotBePriced()
    {
        
        Assert.Null(InitGrindingOptHandler.ResetPrice(0, 629, 36559u, ShippedGrindingInfo, discRate: 0));
    }
}
