using RO2.Domain.Entities;
using RO2.Domain.GameData;
using RO2.Server.Zone.Handlers.Rmi;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Validation;

/// <summary>
/// Rule tests for the shared TradeGate using synthetic item rows. Verifies that the trade handler delegates to the gate.
/// </summary>
public class TradeGateTests
{
    private static ItemData Tpl(byte isTrade = 1, byte bind = 0) => new() { ID = 1, Is_Trade = isTrade, Bind_Type = bind };
    private static InventoryItem Row(byte storage = 0, bool bound = false) => new() { ItemId = 1, StorageType = storage, Bound = bound };

    [Fact]
    public void IsTradeZeroIsRefusedWhateverTheInstance()
    {
        Assert.True(TradeGate.RefusedAsNotTradeable(Tpl(isTrade: 0), Row()));
        Assert.True(TradeGate.RefusedAsNotTradeable(Tpl(isTrade: 0, bind: 1), Row(bound: true)));
    }

    [Fact]
    public void BindType2IsRefusedInTheBag()
        => Assert.True(TradeGate.RefusedAsNotTradeable(Tpl(bind: 2), Row()));

    [Fact]
    public void BindType1IsRefusedOnlyOnceWornOrBound()
    {
        var t = Tpl(bind: 1);
        Assert.False(TradeGate.RefusedAsNotTradeable(t, Row()));                       
        Assert.True(TradeGate.RefusedAsNotTradeable(t, Row(bound: true)));             
        Assert.True(TradeGate.RefusedAsNotTradeable(t, Row(storage: 1)));              
        Assert.True(TradeGate.RefusedAsNotTradeable(t, Row(storage: 3)));              
        Assert.False(TradeGate.RefusedAsNotTradeable(t, Row(storage: 2)));             
    }

    [Fact]
    public void TradeableUnboundItemPasses()
    {
        Assert.False(TradeGate.RefusedAsNotTradeable(Tpl(), Row()));
        Assert.False(TradeGate.RefusedAsNotTradeable(Tpl(bind: 0), Row(storage: 2)));
    }

    [Theory]
    [InlineData(1, 0, 0, false)]
    [InlineData(0, 0, 0, true)]
    [InlineData(1, 2, 0, true)]
    [InlineData(1, 1, 0, false)]
    public void TradeMoveHandlerDelegatesToTheSharedGate(byte isTrade, byte bind, byte storage, bool expected)
    {
        var t = Tpl(isTrade, bind);
        var r = Row(storage);
        Assert.Equal(expected, TradeGate.RefusedAsNotTradeable(t, r));
        Assert.Equal(expected, TradeMoveHandler.RefusedAsNotTradeable(t, r));
    }
}
