using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// The two frames sent to the mover between its C2S 1420 ReqChannelMove and the 2556 answer: 2938 and a
/// self-addressed 2505, which ChannelMoveHandler also sends. Both precede 2556 in a successful move, with
/// 2938 normally first.
/// </summary>
/// <remarks>
/// Vectors are one reference exchange: 1420, then 2938, 2505 and 2556 in that order. The mover's handle in
/// the exchange is .
/// </remarks>
public class ChannelMoveExit1420Tests
{
    private const uint MoverHandle = 0x0B882611u;

    [Fact]
    public void SelfOutList2505_IsTheFrameRetailSendsBefore2556()
    {
        var expected = Convert.FromHexString("010e0e00cfe31126880b01001126880b");
        Assert.Equal(expected, PlayerSpawnService.BuildActorOutList(MoverHandle, new[] { MoverHandle }));
    }
}
