using RO2.Network.Protocol;
using RO2.Server.Zone.Services;
using Xunit;

namespace RO2.Tests.Packets;

/// <summary>
/// The departure frames WarpEntryBurst sends for a /escape (1677) to another map, before its world-entry burst:
/// 2938 <c>01 00</c>, then the self 2505.
/// </summary>
/// <remarks>
/// Reference sequence: C2S 1677 (empty body), then S2C 2938 <c>0100</c> and 2505 <c>010e0e00cfe31105ca0701001105ca07</c>,
/// then 2558 2743 2595 2498 2669, the 2311 to map 1101, 2472 2523 2522, and 2913. The mover's handle is carried by the
/// 2558 and the 2311 of the same burst. Every 1677 in the reference set produces this pair, and the frame after the
/// 2938 is the self list in every window where it is a 2505.
/// </remarks>
public class WarpEntryBurst1677Tests
{
    private const uint MoverHandle = 0x07CA0511u;

    [Fact]
    public void DepartureFrames_AreRetails2938ThenTheSelf2505()
    {
        var frames = WarpEntryBurst.DepartureFrames(MoverHandle);

        Assert.Equal(2, frames.Count);
        Assert.Equal(ZoneS2COpcodes.AnsSiegeArmsUseEnd, frames[0].Opcode);
        Assert.Equal(Convert.FromHexString("0100"), frames[0].Body);
        Assert.Equal(ZoneS2COpcodes.OnPC_Out, frames[1].Opcode);
        Assert.Equal(Convert.FromHexString("010e0e00cfe31105ca0701001105ca07"), frames[1].Body);
    }
}
