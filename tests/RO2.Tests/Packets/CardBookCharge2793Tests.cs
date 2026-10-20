using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// Byte-exact checks for RMI 2793 NfyCardBookChargedTime (<see cref="CardBookChargeStore.Build2793"/>). Each
/// expected body is a reference frame. The expiries are the f32 values those frames carry, so the round trip
/// through DateTime is exact (an f32 step at these dates is 1/256 day, or 337,500 ms).
/// </summary>
public class CardBookCharge2793Tests
{
    [Fact]
    public void Charged_ReproducesTheWorldEntryFrameOfAStillChargedBook()
    {
        
        
        var expected = Convert.FromHexString("010053833447");
        var until = DateTime.FromOADate(46211.32421875);
        Assert.Equal(expected, CardBookChargeStore.Build2793(until, until.AddDays(-1)));
    }

    [Fact]
    public void Charged_ReproducesARetailAnswerTo1548()
    {
        
        var expected = Convert.FromHexString("0100dd883447");
        var until = DateTime.FromOADate(46216.86328125);
        Assert.Equal(expected, CardBookChargeStore.Build2793(until, until.AddDays(-10)));
    }

    [Fact]
    public void Lapsed_ReproducesTheLapsedWorldEntryFrame()
    {
        
        var expected = Convert.FromHexString("030000000000");
        var until = DateTime.FromOADate(46211.32421875);
        Assert.Equal(expected, CardBookChargeStore.Build2793(until, until.AddMinutes(40)));
    }
}
