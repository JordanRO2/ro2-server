using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Byte-exact checks for the trade refusal answers, each compared against a reference S2C body.
/// </summary>
public class TradeRefusalPacketTests
{
    [Theory]
    
    
    [InlineData("010e0e00b2e200000000000011ceeb06", 0x06EBCE11u, (short)0)]
    [InlineData("010e0e00b2e2000000000e001156be08", 0x08BE5611u, TradePackets.OpenTradeRefusedBusy)]
    [InlineData("010e0e00b2e2000000000a0011383a07", 0x073A3811u, TradePackets.OpenTradeRefusedUnavailable)]
    public void OnOpenTradePC_ReproducesRetailFrames(string hex, uint targetHandle, short result)
    {
        Assert.Equal(Convert.FromHexString(hex), TradePackets.BuildOnOpenTradePC(targetHandle, result));
    }

    [Theory]
    
    [InlineData("01121200b5e2000000000e0011d53a0711d83a07", 0x073AD511u, 0x073AD811u, TradeNotifyNotifyPackets.BusyResultCode)]
    [InlineData("01121200b5e2000000000a00112b3a0711383a07", 0x073A2B11u, 0x073A3811u, TradeNotifyNotifyPackets.StaleRequestResultCode)]
    public void OnBeginTradePCNotify_RefusalsReproduceRetailFrames(string hex, uint initiator, uint accepter, ushort code)
    {
        Assert.Equal(Convert.FromHexString(hex), TradeNotifyNotifyPackets.BuildOnBeginTradePCNotify(initiator, accepter, code));
    }

    [Fact]
    public void OnDragTradePC_RepeatedOfferReproducesRetailResult1Frame()
    {
        
        
        
        string tail = "32588c0da83d040e0f202c040000fa0219" + new string('0', 2 * 109);
        var accepted = Convert.FromHexString("028c008c00b7e2000000000000112b3a07" + tail);
        var refused = Convert.FromHexString("028c008c00b7e2000000000100112b3a07" + tail);
        Assert.Equal(143, refused.Length);
        var record = refused.AsSpan(17).ToArray();

        Assert.Equal(accepted, TradePackets.BuildOnDragTradePC(0x073A2B11u, record, 0));
        Assert.Equal(refused, TradePackets.BuildOnDragTradePC(0x073A2B11u, record, TradePackets.DragRefused));
    }

    [Fact]
    public void OnDragTradePC_NotTradeableReproducesRetailFrameAndRecord()
    {
        
        
        var expected = Convert.FromHexString(
            "028c008c00b7e2000000005f4e11875b099babf50ca83d040e2dd9fe00000001" + new string('0', 2 * 111));
        Assert.Equal(143, expected.Length);

        var record = TradePackets.BuildRefusedDragRecord(0x0E043DA80CF5AB9Bul, 16701741u, 1);
        Assert.Equal(126, record.Length);
        Assert.Equal(expected, TradePackets.BuildOnDragTradePC(0x095B8711u, record, TradePackets.DragItemNotTradeable));
    }

    [Fact]
    public void OnConfirmTradePC_CancelCarriesTheRecipientsOwnHandle()
    {
        
        Assert.Equal(Convert.FromHexString("010d0d00bfe200000000112b3a0700"),
            TradePackets.BuildOnConfirmTradePC(0x073A2B11u, 0));
    }
}
