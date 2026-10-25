using RO2.Economy.Domain;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Byte-exact checks for 2387 OnLockTradePC, 2388 OnConfirmTradePC and 2389 OnConfirmTradePCNotify, each compared against a
/// reference S2C body. Also replays one lock round through the server-side trade model to show that the model produces
/// those bytes.
/// </summary>
public class TradeLockConfirmPacketTests
{
    
    private const uint Initiator = 0x073A2B11u, Target = 0x073A2911u;

    [Theory]
    
    [InlineData("010e0e00bde2000000000011293a0701", Target, false, true)]
    
    [InlineData("010e0e00bde20000000001112b3a0701", Initiator, true, true)]
    
    [InlineData("010e0e00bde20000000000111a670700", 0x07671A11u, false, false)]
    public void OnLockTradePC_ReproducesRetailFrames(string hex, uint handle, bool bothLocked, bool locked)
    {
        Assert.Equal(Convert.FromHexString(hex), TradePackets.BuildOnLockTradePC(handle, bothLocked, locked));
    }

    [Theory]
    
    
    [InlineData("010d0d00bfe200000000112b3a0701", Initiator)]
    [InlineData("010d0d00bfe20000000011293a0701", Target)]
    public void OnConfirmTradePC_ConfirmNamesTheConfirmer(string hex, uint confirmer)
    {
        Assert.Equal(Convert.FromHexString(hex), TradePackets.BuildOnConfirmTradePC(confirmer, 1));
    }

    [Fact]
    public void RetailLockRoundReplaysThroughTheTradeModel()
    {
        
        
        
        
        var svc = new TradeSessionService();
        var trade = svc.Request(1, 2, Initiator, Target, _ => true);
        Assert.True(svc.TryAccept(trade));

        var targetLock = trade.SetLocked(2, true);
        Assert.True(targetLock.Applied);
        Assert.Equal(Convert.FromHexString("010e0e00bde2000000000011293a0701"),
            TradePackets.BuildOnLockTradePC(trade.HandleFor(2), targetLock.BothLocked, true));

        Assert.Equal(TradeOfferChange.Applied, trade.AddItem(1, 0x0E043DA80D8D305Bul, 180));
        Assert.Equal(TradeOfferChange.Applied, trade.AddItem(1, 0x0E043DA80D8C5832ul, 250));
        Assert.Equal(TradeOfferChange.AlreadyOffered, trade.AddItem(1, 0x0E043DA80D8C5832ul, 250));

        Assert.Equal(TradeOfferChange.Applied, trade.SetGold(1, 0));
        var initiatorLock = trade.SetLocked(1, true);
        Assert.True(initiatorLock.Applied);
        Assert.Equal(Convert.FromHexString("010e0e00bde20000000001112b3a0701"),
            TradePackets.BuildOnLockTradePC(trade.HandleFor(1), initiatorLock.BothLocked, true));

        Assert.Equal(TradeOfferChange.SideLocked, trade.SetGold(1, 0));
        Assert.False(trade.SetLocked(1, true).Applied);

        
        Assert.False(trade.ConfirmAndTryComplete(1));
        Assert.True(trade.ConfirmAndTryComplete(2));
    }

    [Fact]
    public void RepeatedUnlockIsNotApplied()
    {
        
        var svc = new TradeSessionService();
        var trade = svc.Request(1, 2, Initiator, Target, _ => true);
        Assert.True(svc.TryAccept(trade));

        Assert.False(trade.SetLocked(1, false).Applied); 
        Assert.True(trade.SetLocked(1, true).Applied);
        var unlock = trade.SetLocked(1, false);
        Assert.True(unlock.Applied);
        Assert.False(unlock.BothLocked);
        Assert.False(trade.SetLocked(1, false).Applied);
        Assert.False(trade.IsLocked(1));
    }

    [Fact]
    public void OnConfirmTradePCNotify_ReproducesRetailFrame()
    {
        
        
        
        
        string Rec(string hex) => hex + new string('0', 252 - hex.Length);
        var records = new[]
        {
            "89303a41a73d040ee2f1c100000001000e0000000000000f122cff08" + new string('0', 186) + "010f722647",
            Rec("0df6812aa73d040e796c54011027010014"),
            Rec("db695d09a83d040e8288930300000101460000000000000000000002"),
            Rec("286a5d09a83d040e8288930300000101470000000000000000000002"),
            Rec("616b5d09a83d040e8288930300000101480000000000000000000002"),
        };
        var expected = Convert.FromHexString(
            "8e020000" + "0000" + "117fce0a" + "5efa000000000000" + "5efa000000000000" + "0500" + string.Concat(records));
        Assert.Equal(658, expected.Length);

        var items = records.Select(h => Convert.FromHexString(h)).ToList();
        Assert.All(items, r => Assert.Equal(126, r.Length));
        Assert.Equal(expected, TradeNotifyNotifyPackets.BuildOnConfirmTradePCNotify(0, 0x0ACE7F11u, 64094ul, items));
    }
}
